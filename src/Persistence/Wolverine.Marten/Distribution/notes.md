# Scalable Projection Distributor

## Notes

* Projections that are revisioned, how do we discover them?
* Make Marten on activation, persist all shard name progress? Discover from there?
* All known supported Uri is passed to `AssignmentGrid`. Probably requires changes to Wolverine, but start from
  integration tests w/ CritterStackPro

## Next Steps

1. Add `MultipleTenantContext`. Copy paste around code to create databases. Start by making it static
2. Test distribution by database
3. Add `startGreenHostAsync()` to new `TenantContext` with different `TripProjection`, see new versioned projections
   running on green even if the original leader is "blue"
4. Unit test hard on assignment grid. Maybe a helper base class for that?
5. Take down nodes, see them move projections
6. Take down leader, see projections move
7. In blue/green with single tenant, take down leader
8. In blue/green with multiple tenant, take down leader
9. With multi-tenancy, add a new database at runtime and see that the agent is started *somewhere*.

## Critter Stack Pro Use Cases (Phase 1)

1. Single database, distribute running asynchronous projections evenly across running nodes
2. Multi-tenancy with separate databases, distribute running asynchronous projections by database across running nodes
3. In blue/green deployments, allow for running separate versions of the same named projection on the blue or green
   nodes
4. In blue/green deployments, allow for all new projections
4. Discover new tenant databases at runtime and distribute async projection running for the new databases

## Wolverine Agent Distribution

* `AssignmentGrid` needs to declare/assert/find what nodes can support nodes. For the blue/green
* For the blue/green deployment, if there is any projection that can only be supported on a subset of nodes, spread
  those
  evenly. Use `IProjectionDistributor` otherwise
