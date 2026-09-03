/*
What the election owes the cluster. See the README; asserted at quiescence, because two
nodes can transiently both believe they lead (GH-2602).

  Safety   — one live node believes it leads, holds the lock, and alone carries the leader
             mark; every live node has a row; no stale or orphaned row survives.
  Liveness — quiescence is reached (the hot state catches a wedged lock or nodes trading
             leadership forever).
*/

event eMRoster: (count: int);
event eMNodeState: (id: int, alive: bool, isLeader: bool, busy: bool, newFaults: int);
event eMStoreState: (lockHolder: int, rows: map[int, tRow], newFaultsDone: int, newRefills: int);
event eMRefillDone;

spec ElectionConverges observes eMRoster, eMNodeState, eMStoreState, eMRefillDone {
  var roster: int;
  var alive: map[int, bool];
  var believesLeader: map[int, bool];
  var busy: map[int, bool];
  var lockHolder: int;
  var rows: map[int, tRow];
  /* Fault effects announced by a dying node (its session reaper, its stale marker) that
     the store has not yet absorbed. */
  var pendingFaults: int;
  /* Refill waves the store has sent that nodes have not yet consumed. */
  var pendingRefills: int;

  start cold state Quiet {
    on eMRoster do (p: (count: int)) { rosterSet(p); }
    on eMNodeState do (p: (id: int, alive: bool, isLeader: bool, busy: bool, newFaults: int)) { nodeState(p); }
    on eMStoreState do (p: (lockHolder: int, rows: map[int, tRow], newFaultsDone: int, newRefills: int)) { storeState(p); }
    on eMRefillDone do { refillDone(); }
  }

  hot state Working {
    on eMRoster do (p: (count: int)) { rosterSet(p); }
    on eMNodeState do (p: (id: int, alive: bool, isLeader: bool, busy: bool, newFaults: int)) { nodeState(p); }
    on eMStoreState do (p: (lockHolder: int, rows: map[int, tRow], newFaultsDone: int, newRefills: int)) { storeState(p); }
    on eMRefillDone do { refillDone(); }
  }

  fun rosterSet(p: (count: int)) {
    roster = p.count;
    check();
  }

  fun nodeState(p: (id: int, alive: bool, isLeader: bool, busy: bool, newFaults: int)) {
    alive[p.id] = p.alive;
    believesLeader[p.id] = p.isLeader;
    busy[p.id] = p.busy;
    pendingFaults = pendingFaults + p.newFaults;
    check();
  }

  fun storeState(p: (lockHolder: int, rows: map[int, tRow], newFaultsDone: int, newRefills: int)) {
    lockHolder = p.lockHolder;
    rows = p.rows;
    pendingFaults = pendingFaults - p.newFaultsDone;
    pendingRefills = pendingRefills + p.newRefills;
    check();
  }

  fun refillDone() {
    pendingRefills = pendingRefills - 1;
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
    if (pendingFaults > 0 || pendingRefills > 0) {
      return false;
    }
    foreach (i in keys(busy)) {
      if (busy[i]) {
        return false;
      }
    }
    return true;
  }

  fun converged() {
    var i: int;
    var leader: int;
    var leaders: int;
    var liveCount: int;

    foreach (i in keys(alive)) {
      if (alive[i]) {
        liveCount = liveCount + 1;
        if (believesLeader[i]) {
          leaders = leaders + 1;
          leader = i;
        }
      }
    }

    /* A fully-stopped cluster owes nothing except a freed lock. */
    if (liveCount == 0) {
      assert lockHolder == 0,
        format("every node is gone, yet the advisory lock is still held by session {0}", lockHolder);
      return;
    }

    assert leaders == 1,
      format("settled with {0} live nodes believing they are the leader", leaders);
    assert lockHolder == leader,
      format("node {0} settled as leader while the store's advisory lock belongs to {1}", leader, lockHolder);
    assert leader in rows && rows[leader].leaderMark,
      format("node {0} settled as leader but its row does not carry the leader mark", leader);

    foreach (i in keys(rows)) {
      assert !rows[i].stale,
        format("a stale row for node {0} survived quiescence — never ejected, never refreshed", i);
      assert i in alive && alive[i],
        format("a row for dead node {0} survived quiescence — a departed node was never cleaned up", i);
      assert !rows[i].leaderMark || i == leader,
        format("node {0}'s row still carries the leader mark while node {1} is the leader", i, leader);
    }

    foreach (i in keys(alive)) {
      if (alive[i]) {
        assert i in rows,
          format("live node {0} has no row at quiescence — ejected and never resurrected", i);
      }
    }
  }
}
