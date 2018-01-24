﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Consensus.Contract.Messages;

[assembly: KnownType(typeof(NotLeaderException))]

namespace Orleans.Consensus.Roles
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using Orleans.Consensus.Actors;
    using Orleans.Consensus.Contract;
    using Orleans.Consensus.Contract.Log;
    using Orleans.Consensus.Contract.Messages;
    using Orleans.Consensus.State;
    using Orleans.Consensus.Utilities;

    internal class LeaderRole<TOperation> : ILeaderRole<TOperation>
    {
        private readonly Stopwatch callTimer = new Stopwatch();

        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

        private readonly IRoleCoordinator<TOperation> coordinator;

        private readonly IGrainFactory grainFactory;

        private readonly TimeSpan heartbeatTimeout;

        private readonly IServerIdentity identity;

        private readonly IPersistentLog<TOperation> journal;

        private readonly ILogger logger;

        private readonly IMembershipProvider membershipProvider;

        private readonly ReplicaSetOptions replicaSetOptions;

        private readonly IRaftPersistentState persistentState;

        private readonly Dictionary<string, FollowerProgress> followers = new Dictionary<string, FollowerProgress>();

        private readonly IStateMachine<TOperation> stateMachine;

        private readonly IRaftVolatileState volatileState;

        private IDisposable heartbeatTimer;

        private DateTime lastMessageSentTime;

        private bool madeProgress;

        private readonly RegisterTimerDelegate registerTimer;

        public LeaderRole(
            IRoleCoordinator<TOperation> coordinator,
            ILogger<LeaderRole<TOperation>> logger,
            IPersistentLog<TOperation> journal,
            IStateMachine<TOperation> stateMachine,
            IRaftPersistentState persistentState,
            IRaftVolatileState volatileState,
            IGrainFactory grainFactory,
            RegisterTimerDelegate registerTimer,
            IServerIdentity identity,
            IMembershipProvider membershipProvider,
            IOptions<ReplicaSetOptions> replicaSetOptions)
        {
            this.coordinator = coordinator;
            this.logger = logger;
            this.journal = journal;
            this.stateMachine = stateMachine;
            this.persistentState = persistentState;
            this.volatileState = volatileState;
            this.grainFactory = grainFactory;
            this.registerTimer = registerTimer;
            this.identity = identity;
            this.membershipProvider = membershipProvider;
            this.replicaSetOptions = replicaSetOptions.Value;
            this.heartbeatTimeout = TimeSpan.FromMilliseconds(this.replicaSetOptions.HeartbeatTimeoutMilliseconds);
        }

        private int QuorumSize => (this.followers.Count + 1) / 2;

        public string RoleName => "Leader";

        public async Task Enter()
        {
            this.logger.LogInformation("Becoming leader.");

            // Initialize volatile state on leader.
            foreach (var server in this.membershipProvider.OtherServers)
            {
                this.followers[server] = new FollowerProgress
                {
                    NextIndex = this.journal.LastLogEntryId.Index + 1,
                    MatchIndex = 0
                };
            }

            // This node is the leader.
            this.volatileState.LeaderId = this.identity.Id;

            // Upon election: send initial empty AppendEntries RPCs (heartbeat) to each server; repeat during idle
            // periods to prevent election timeouts (§5.2)
            this.heartbeatTimer?.Dispose();
            this.heartbeatTimer = this.registerTimer(
                _ => this.SendHeartBeats(),
                null,
                TimeSpan.Zero,
                this.heartbeatTimeout);

            if (this.stateMachine != null)
            {
                await this.stateMachine.Reset();
            }
        }

        public Task Exit()
        {
            this.logger.LogWarning("Leaving leader state.");
            this.heartbeatTimer?.Dispose();
            this.cancellation.Cancel();
            return Task.FromResult(0);
        }

        public async Task<RequestVoteResponse> RequestVote(RequestVoteRequest request)
        {
            // If RPC request or response contains term T > currentTerm: set currentTerm = T, convert to follower (§5.1)
            if (await this.coordinator.StepDownIfGreaterTerm(request))
            {
                return await this.coordinator.Role.RequestVote(request);
            }

            return new RequestVoteResponse { VoteGranted = false, Term = this.persistentState.CurrentTerm };
        }

        public async Task<AppendResponse> Append(AppendRequest<TOperation> request)
        {
            // If RPC request or response contains term T > currentTerm: set currentTerm = T, convert to follower (§5.1)
            if (await this.coordinator.StepDownIfGreaterTerm(request))
            {
                return await this.coordinator.Role.Append(request);
            }

            this.logger.LogWarning($"Denying append from {request.Leader}.");
            return new AppendResponse { Success = false, Term = this.persistentState.CurrentTerm };
        }

        /// <summary>
        /// Replicates a collection of operations to followers.
        /// </summary>
        /// <param name="operations"></param>
        /// <returns></returns>
        public async Task<LogEntry<TOperation>[]> ReplicateOperations(TOperation[] operations)
        {
            LogEntry<TOperation>[] entries;
            if (operations != null && operations.Length > 0)
            {
                // Assign each operation an identifier, converting it into a log entry.
                var nextIndex = this.journal.LastLogEntryId.Index + 1;
                entries = new LogEntry<TOperation>[operations.Length];
                for (var i = 0; i < entries.Length; i++)
                {
                    entries[i] = new LogEntry<TOperation>(new LogEntryId(this.persistentState.CurrentTerm, nextIndex++), operations[i]);
                }
            }
            else
            {
                entries = null;
            }

            await this.ReplicateEntries(entries);
            return entries;
        }

        private async Task ReplicateEntries(LogEntry<TOperation>[] entries)
        {
            if (entries != null && entries.Length > 0)
            {
                this.logger.LogInformation($"Replicating {entries.Length} entries to {this.followers.Count} servers");
                await this.journal.AppendOrOverwrite(entries);

#warning account for candidate and failed (overwritten) configuration entries

                this.logger.LogInformation($"Leader log is: {this.journal.ProgressString()}");
            }

            await this.Replicate();
        }

        /// <summary>
        /// Performs a round of replication attempts on all followers.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        private async Task Replicate()
        {
            var tasks = new List<Task>(this.followers.Count);
            foreach (var server in this.followers)
            {
                tasks.Add(this.AppendEntriesOnServer(server.Key, server.Value));
            }

            await Task.WhenAll(tasks);
            if (this.madeProgress)
            {
                await this.UpdateCommittedIndex();
                this.madeProgress = false;
            }
        }

        public Task SendHeartBeats()
        {
            this.logger.LogTrace("heartbeat");
            if (this.lastMessageSentTime + this.heartbeatTimeout > DateTime.UtcNow)
            {
                // Only send heartbeat if idle.
                return Task.FromResult(0);
            }

            // Send a heartbeat.
            return this.ReplicateOperations(null);
        }

        private async Task AppendEntriesOnServer(string serverId, FollowerProgress followerProgress)
        {
            this.callTimer.Restart();
            var serverGrain = this.grainFactory.GetGrain<IRaftGrain<TOperation>>(serverId);
            while (!this.cancellation.IsCancellationRequested)
            {
                var nextIndex = followerProgress.NextIndex;
                var request = new AppendRequest<TOperation>
                {
                    Leader = this.identity.Id,
                    LeaderCommitIndex = this.volatileState.CommitIndex,
                    Term = this.persistentState.CurrentTerm,
                    Entries =
                        this.journal.GetCursor((int)Math.Max(0, nextIndex - 1))
                            .Take(this.replicaSetOptions.MaxLogEntriesPerAppendRequest)
                            .ToArray()
                };

                if (nextIndex >= 2 && this.journal.LastLogEntryId.Index > nextIndex - 2)
                {
                    request.PreviousLogEntry = this.journal.Get((int)nextIndex - 1).Id;
                }

                this.lastMessageSentTime = DateTime.UtcNow;
                if (request.Entries.Length > 0)
                {
                    this.logger.LogInformation(
                        $"Replicating to '{serverId}': prev: {request.PreviousLogEntry},"
                        + $" entries: [{string.Join(", ", request.Entries.Select(_ => _.Id))}]"
                        + $", next: {nextIndex}, match: {followerProgress.MatchIndex}");
                }

                var response = await serverGrain.Append(request);
                if (response.Success)
                {
                    // The follower's log matches the included logs.
                    var newMatchIndex = Math.Max(
                        followerProgress.MatchIndex,
                        (long)request.Entries?.LastOrDefault().Id.Index);

                    // The send was successful, so the next entry to send is the subsequent entry in the leader's log.
                    followerProgress.NextIndex = newMatchIndex + 1;

                    // For efficiency, only consider updating the committedIndex if matchIndex has changed.
                    if (newMatchIndex != followerProgress.MatchIndex)
                    {
                        this.logger.LogInformation(
                            $"Successfully appended entries {request.Entries?.FirstOrDefault().Id} to {newMatchIndex} on {serverId}");
                        followerProgress.MatchIndex = newMatchIndex;

                        this.madeProgress = true;
                    }

                    return;
                }

                this.logger.LogWarning($"Received failure response for append call with term of {response.Term}");
                if (await this.coordinator.StepDownIfGreaterTerm(response))
                {
                    // This node is no longer a leader, so retire.
                    return;
                }

                if (followerProgress.NextIndex > response.LastLogEntryId.Index)
                {
                    // If the follower is lagging, jump immediately to its last log entry.
                    followerProgress.NextIndex = response.LastLogEntryId.Index;
                    this.logger.LogInformation(
                        $"Follower's last log is {response.LastLogEntryId}, jumping nextIndex to that index.");
                }
                else if (followerProgress.NextIndex > 0)
                {
                    // Log mismatch, decrement and try again later.
                    --followerProgress.NextIndex;
                    this.logger.LogWarning(
                        $"Log mismatch must have occured on '{serverId}', decrementing nextIndex to {followerProgress.NextIndex}.");
                }

                // In an attempt to maintain fairness and retain leader status, bail out of catchup if the call has taken
                // more than the allotted time.
                if (this.callTimer.ElapsedMilliseconds
                    >= this.replicaSetOptions.HeartbeatTimeoutMilliseconds / (this.membershipProvider.AllServers.Count - 1))
                {
                    this.logger.LogInformation(
                        $"Bailing on '{serverId}' catch-up due to fairness. Elapsed: {this.callTimer.ElapsedMilliseconds}.");
                    break;
                }
            }
        }

        private Task UpdateCommittedIndex()
        {
            foreach (var entry in this.journal.GetReverseCursor())
            {
                if (entry.Id.Term != this.persistentState.CurrentTerm)
                {
                    // Only log entries from the leader’s current term are committed by counting replicas. See §5.4.2.
                    return Task.FromResult(0);
                }

                // Return if the log entry is already committed.
                if (entry.Id.Index == this.volatileState.CommitIndex)
                {
                    return Task.FromResult(0);
                }

                var index = entry.Id.Index;
                var replicas = 0;
                foreach (var server in this.followers)
                {
                    // If the server is not known to have this log entry, continue to the next server.
                    if (server.Value.MatchIndex < index)
                    {
                        continue;
                    }

                    replicas++;

                    // If a quorum has not yet been reached, continue to the next server.
                    if (replicas < this.QuorumSize)
                    {
                        continue;
                    }

                    // This entry is committed.
                    this.logger.LogInformation(
                        $"Recently committed entries from {this.volatileState.CommitIndex} to {index} with {replicas + 1}/{this.followers.Count + 1} replicas.");
                    this.volatileState.CommitIndex = index;
                    return this.ApplyRemainingCommittedEntries();
                }
            }

            return Task.FromResult(0);
        }

        private async Task ApplyRemainingCommittedEntries()
        {
            if (this.stateMachine != null)
            {
                foreach (var entry in
                    this.journal.GetCursor((int)this.volatileState.LastApplied)
                        .Take((int)(this.volatileState.CommitIndex - this.volatileState.LastApplied)))
                {
                    this.logger.LogInformation($"Applying {entry}.");
                    if (entry.IsOperation)
                    {
                        await this.stateMachine.Apply(entry);
                    }

                    if (entry.IsConfiguration)
                    {
#warning account for successful configuration commit.
                    }

                    this.volatileState.LastApplied = entry.Id.Index;
                }
            }
        }

        internal class FollowerProgress
        {
            public long NextIndex { get; set; }
            public long MatchIndex { get; set; }
        }
    }
}