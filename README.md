# Orleans.Consensus
[![Build status](https://ci.appveyor.com/api/projects/status/6hjgxgc6rjnkd5hg?svg=true)](https://ci.appveyor.com/project/ReubenBond/orleans-consensus)

A library for creating fault-tolerant, strong-consistency, persistent services with [Orleans](https://github.com/dotnet/orleans).
Orleans.Consensus implements a replicated log using the [Raft](https://raft.github.io/) consensus algorithm, allowing consumers to turn single-node services into distributed services.

> Note that that this is currently a pre-alpha work-in-progress which you should not use for anything, but you should help us build it :)

## Goal
* Fault-tolerant: services should be available during partial cluster failures and should recover promptly without user intervention.
* Durable: acknowledged writes must not be lost, even in the event of a temporary total cluster failure.
* Scalable: each replicated service should have minimal overhead, particularly when not actively replicating operations.
* Responsive: replicated services should respond promptly to requests (of course, this depends on the environment).
* Extensible: consumers should feel comfortable creating their own services using this library.

## Usage

Open project in VS, hit F5, watch the debug output.

## Status

* ~~Extract interfaces for the concrete classes and clean them up.~~
* ~~Autofac for dependency injection to make the system more testable.~~
* Extensive automated testing.
  * ~~Follower~~/~~Candidate~~/Leader role behavior
  * ~~RoleCoordinator~~
  * System tests with fault injection.
* Use Orleans to provide cluster membership.
* Implement Joint-Consensus for cluster membership change from the Raft paper.
* Implement a physical log (it's all in-memory at the moment) - and make sure it's cross-platform! Probably have one physical log per cluster based on Sqlite or another xplat embeddable db (suggestions?).
* Implement log compaction.
* Extend the Raft log repair algorithm (where leaders fix followers' logs) so that the client returns a Progress Vector on failure instead of just false. Then use that progress vector on the leader to select the best position in the client's log to update `nextIndex` to.
* Implement non-voting followers ("listeners" ?). This allows for new servers to be added without decreasing the ratio of up-to-date followers and lagging followers. Lagging followers slow down the commit process without significantly increasing fault tolerance: they are counted in the quorum and yet do not have current log entries yet and so a higher proportion of the up-to-date followers need to acknowledge each new log entry in order for it to be committed. Adding too many lagging replicas too quickly would cause the cluster to stall while they are brought up-to-date. 

## License

MIT
