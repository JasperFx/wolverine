/*
What single-agent ownership owes the cluster. See the README; asserted at quiescence,
because two runners are unavoidable mid-partition.

  Safety   — one live node runs the agent, holds the one row, no other row survives.
  Liveness — quiescence is reached (the hot state catches a duplicate never healed, an
             unowned agent never placed, or the leader churning).
*/

event eMRoster: (count: int);
event eMNode: (id: int, localRun: bool, partitioned: bool, alive: bool, busy: bool);
event eMStore: (leader: int, rows: map[int, bool], live: map[int, bool], newRuns: int, newRefills: int);
event eMRunDone;
event eMRefillDone;
/* A node's durable row write (add/remove) in flight to the store, and its completion. */
event eMRowPending;
event eMRowDone;
/* A crash or partition in effect but not yet fully absorbed (+1 opens, -1 closes). */
event eMFault: (delta: int);

spec OwnershipConverges observes eMRoster, eMNode, eMStore, eMRunDone, eMRefillDone, eMRowPending, eMRowDone, eMFault {
  var roster: int;
  var localRun: map[int, bool];
  var partitioned: map[int, bool];
  var alive: map[int, bool];
  var busy: map[int, bool];
  var rows: map[int, bool];
  var live: map[int, bool];
  /* Run-state commands emitted by the store but not yet applied by their target. */
  var pendingRuns: int;
  /* Refill waves sent but not yet consumed. */
  var pendingRefills: int;
  /* Durable row writes emitted by a node but not yet applied by the store. */
  var pendingRows: int;
  /* Crashes/partitions injected but not yet fully absorbed. */
  var pendingFaults: int;

  start cold state Quiet {
    on eMRoster do (p: (count: int)) { roster = p.count; check(); }
    on eMNode do (p: (id: int, localRun: bool, partitioned: bool, alive: bool, busy: bool)) { node(p); }
    on eMStore do (p: (leader: int, rows: map[int, bool], live: map[int, bool], newRuns: int, newRefills: int)) { st(p); }
    on eMRunDone do { pendingRuns = pendingRuns - 1; check(); }
    on eMRefillDone do { pendingRefills = pendingRefills - 1; check(); }
    on eMRowPending do { pendingRows = pendingRows + 1; check(); }
    on eMRowDone do { pendingRows = pendingRows - 1; check(); }
    on eMFault do (p: (delta: int)) { pendingFaults = pendingFaults + p.delta; check(); }
  }

  hot state Working {
    on eMRoster do (p: (count: int)) { roster = p.count; check(); }
    on eMNode do (p: (id: int, localRun: bool, partitioned: bool, alive: bool, busy: bool)) { node(p); }
    on eMStore do (p: (leader: int, rows: map[int, bool], live: map[int, bool], newRuns: int, newRefills: int)) { st(p); }
    on eMRunDone do { pendingRuns = pendingRuns - 1; check(); }
    on eMRefillDone do { pendingRefills = pendingRefills - 1; check(); }
    on eMRowPending do { pendingRows = pendingRows + 1; check(); }
    on eMRowDone do { pendingRows = pendingRows - 1; check(); }
    on eMFault do (p: (delta: int)) { pendingFaults = pendingFaults + p.delta; check(); }
  }

  fun node(p: (id: int, localRun: bool, partitioned: bool, alive: bool, busy: bool)) {
    localRun[p.id] = p.localRun;
    partitioned[p.id] = p.partitioned;
    alive[p.id] = p.alive;
    busy[p.id] = p.busy;
    check();
  }

  fun st(p: (leader: int, rows: map[int, bool], live: map[int, bool], newRuns: int, newRefills: int)) {
    rows = p.rows;
    live = p.live;
    pendingRuns = pendingRuns + p.newRuns;
    pendingRefills = pendingRefills + p.newRefills;
    check();
  }

  fun check() {
    if (settled()) {
      converged();
      goto Quiet;
    } else {
      goto Working;
    }
  }

  fun settled(): bool {
    var i: int;
    if (roster == 0 || sizeof(alive) < roster) {
      return false;
    }
    if (pendingRuns > 0 || pendingRefills > 0 || pendingRows > 0 || pendingFaults > 0) {
      return false;
    }
    foreach (i in keys(busy)) {
      if (busy[i]) {
        return false;
      }
    }
    /* A partition still in effect is not a quiescent cluster. */
    foreach (i in keys(partitioned)) {
      if (partitioned[i]) {
        return false;
      }
    }
    return true;
  }

  fun converged() {
    var i: int;
    var runners: int;
    var runner: int;
    var rowCount: int;
    var rowNode: int;
    var liveCount: int;

    foreach (i in keys(alive)) {
      if (alive[i]) { liveCount = liveCount + 1; }
    }

    foreach (i in keys(localRun)) {
      if (localRun[i]) {
        runners = runners + 1;
        runner = i;
      }
    }

    foreach (i in keys(rows)) {
      if (rows[i]) {
        rowCount = rowCount + 1;
        rowNode = i;
      }
    }

    if (liveCount == 0) {
      assert runners == 0,
        format("no node is alive, yet {0} still runs the agent", runner);
      assert rowCount == 0,
        format("no node is alive, yet a durable assignment row for node {0} survives", rowNode);
      return;
    }

    assert runners == 1,
      format("settled with {0} nodes running the single agent (expected exactly one)", runners);
    assert alive[runner] && !partitioned[runner],
      format("the agent settled running on node {0}, which is not a healthy live node", runner);
    assert rowCount == 1 && rowNode == runner,
      format("the runner is node {0} but the durable row(s) point elsewhere: {1}", runner, rows);
  }
}
