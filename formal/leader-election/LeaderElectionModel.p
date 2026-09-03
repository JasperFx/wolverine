/*
Wolverine's leader election, as NodeAgentController runs it on main.
See formal/leader-election/README.md for what is abstracted away and why.

Store = the wolverine_nodes table, the LeaderUri assignment row, and the session-scoped
        advisory lock, as one machine — so a P execution serializes storage operations
        exactly the way the database does.
Node  = one Wolverine process: DoHealthChecksInternalAsync as a sequence of storage
        round trips, plus the local IsLeader / lock beliefs those round trips feed.
*/

/* A persisted node row: heartbeat freshness plus whether the row carries the LeaderUri
   assignment (what WolverineNode.IsLeader() reads). */
type tRow = (stale: bool, leaderMark: bool);

/* Bootstrapping: PersistAsync hands out sequential node numbers. */
event eRegisterNode: machine;
event eNodeNumber: (id: int);
event eStart;

/* One health-check tick's round trips, in DoHealthChecksInternalAsync order. */
event eHeartbeat: (node: machine, id: int, isLeader: bool);
event eSnapshot: (rows: map[int, tRow], youHoldLock: bool);
event eEject: (id: int);
event eStepDown: (node: machine, id: int);
event eTryAttain: (node: machine, id: int);
event eLockResult: (granted: bool);
event eClaim: (node: machine, id: int);
event eClaimAck;

/* Graceful shutdown: NodeAgentController.StopAsync. */
event eShutdown;
event eReleaseAndDelete: (node: machine, id: int, hadLock: bool);
event eStopAck;

/* Faults. eSessionDied = the database noticing a dead advisory-lock session; eMarkStale =
   a row's heartbeat aging past StaleNodeTimeout. `counted` = the fault was pre-announced for
   the monitor's quiescence accounting (see the README). */
event eCrash;
event eSessionDied: (id: int, counted: bool);
event eMarkStale: (id: int, counted: bool);
/* Stand-in for "the health-check loop runs forever": every fault refills the surviving
   nodes' tick budgets, so quiescence is reached only after the last fault is absorbed. */
event eRefill;

/*
The database. One machine: arrival order is commit order. The advisory lock is
session-level — eEject (DeleteAsync) deliberately does not touch it, because deleting a
dead leader's row does not free the lock its dead session still holds; only eSessionDied
does that.
*/
machine Store {
  var rows: map[int, tRow];
  var members: map[int, machine];
  /* 0 = free. Node ids start at 1. */
  var lockHolder: int;
  var nextId: int;

  start state Serving {
    entry {
      nextId = 1;
    }

    on eRegisterNode do (node: machine) {
      members[nextId] = node;
      rows[nextId] = (stale = false, leaderMark = false);
      send node, eNodeNumber, (id = nextId,);
      nextId = nextId + 1;
      announceStore(0, 0);
    }

    on eHeartbeat do (m: (node: machine, id: int, isLeader: bool)) {
      /* MarkHealthCheckAsync refreshes an existing row; a miss means a peer ejected a
         still-live node, so ReregisterNodeAsync resurrects it with its real identity —
         leader mark included (GH-3604/D2). The reply folds in the node-state read and the
         HasLeadershipLock ping (GH-2602); see the README on that compression. */
      if (m.id in rows) {
        rows[m.id] = (stale = false, leaderMark = rows[m.id].leaderMark);
      } else {
        rows[m.id] = (stale = false, leaderMark = m.isLeader);
      }
      send m.node, eSnapshot, (rows = rows, youHoldLock = lockHolder == m.id);
      announceStore(0, 0);
    }

    on eEject do (m: (id: int)) {
      /* DeleteAsync: the row and its assignment rows go, the advisory lock does not. */
      if (m.id in rows) {
        rows -= (m.id);
        /* The victim's own loops keep running no matter what peers did to its row; the
           refill is the model's stand-in for those future ticks, and is what lets a
           still-live victim resurrect itself. */
        send members[m.id], eRefill;
        announceStore(0, 1);
      }
    }

    on eStepDown do (m: (node: machine, id: int)) {
      /* stepDownAsync: stop the local leader agent (which drops the LeaderUri assignment
         row) and best-effort release the advisory lock. */
      if (lockHolder == m.id) {
        lockHolder = 0;
      }
      if (m.id in rows) {
        rows[m.id] = (stale = rows[m.id].stale, leaderMark = false);
      }
      announceStore(0, 0);
    }

    on eTryAttain do (m: (node: machine, id: int)) {
      /* A session-level advisory lock: granted when free, and idempotently re-granted to
         the current holder — the client-side short-circuit in
         AdvisoryLock.TryAttainLockAsync that keeps the lock from stacking. */
      if (lockHolder == 0 || lockHolder == m.id) {
        lockHolder = m.id;
        send m.node, eLockResult, (granted = true,);
      } else {
        send m.node, eLockResult, (granted = false,);
      }
      announceStore(0, 0);
    }

    on eClaim do (m: (node: machine, id: int)) {
      /* tryStartLeadershipAsync: ensureLocalNodeRegisteredAsync + AddAssignmentAsync of
         LeaderUri. Runs on a pooled connection, not the lock session — so a claim CAN
         land after the lock has already moved on. The claimant discovers that on its
         next tick's ping and steps down; the monitor tolerates the window and checks
         the outcome at quiescence. */
      if (m.id in rows) {
        rows[m.id] = (stale = rows[m.id].stale, leaderMark = true);
      } else {
        rows[m.id] = (stale = false, leaderMark = true);
      }
      send m.node, eClaimAck;
      announceStore(0, 0);
    }

    on eReleaseAndDelete do (m: (node: machine, id: int, hadLock: bool)) {
      /* StopAsync: release the lock if the node believes it holds it, then delete the
         node's own row. */
      if (m.hadLock && lockHolder == m.id) {
        lockHolder = 0;
      }
      if (m.id in rows) {
        rows -= (m.id);
      }
      send m.node, eStopAck;
      announceStore(0, 0);
    }

    on eSessionDied do (m: (id: int, counted: bool)) {
      var faultsDone: int;
      if (lockHolder == m.id) {
        lockHolder = 0;
      }
      if (m.counted) {
        faultsDone = 1;
      }
      announceStore(faultsDone, refillAll());
    }

    on eMarkStale do (m: (id: int, counted: bool)) {
      var faultsDone: int;
      if (m.id in rows) {
        rows[m.id] = (stale = true, leaderMark = rows[m.id].leaderMark);
      }
      if (m.counted) {
        faultsDone = 1;
      }
      announceStore(faultsDone, refillAll());
    }
  }

  fun refillAll(): int {
    var i: int;
    foreach (i in keys(members)) {
      send members[i], eRefill;
    }
    return sizeof(members);
  }

  fun announceStore(faultsDone: int, refills: int) {
    announce eMStoreState,
      (lockHolder = lockHolder, rows = rows, newFaultsDone = faultsDone, newRefills = refills);
  }
}

/* Couriers: a fault's effect lands at a scheduler-chosen moment, so the lock-freed and
   row-stale halves of one crash can arrive in any order and at any time. */
machine SessionReaper {
  start state Deliver {
    entry (m: (store: machine, id: int, counted: bool)) {
      send m.store, eSessionDied, (id = m.id, counted = m.counted);
    }
  }
}

machine StaleReaper {
  start state Deliver {
    entry (m: (store: machine, id: int, counted: bool)) {
      send m.store, eMarkStale, (id = m.id, counted = m.counted);
    }
  }
}

machine CrashCourier {
  start state Deliver {
    entry (target: machine) {
      send target, eCrash;
    }
  }
}

machine ShutdownCourier {
  start state Deliver {
    entry (target: machine) {
      send target, eShutdown;
    }
  }
}

/*
One Wolverine process. A tick is DoHealthChecksInternalAsync: write own heartbeat and
read the cluster (one round trip here), eject sustained-stale peers, step down if the
lock is gone, then always try to attain-or-renew the lock. The node blocks on each
round trip the way a synchronous database client does, so a node has at most one
outstanding request — but ticks from different nodes interleave freely at the store.
*/
machine Node {
  var store: machine;
  var id: int;
  /* Ticks granted at start and by every refill wave. */
  var k: int;
  var budget: int;
  var isLeader: bool;
  /* The client-side _locks list: belief, refreshed by the snapshot's folded-in ping. */
  var hasLock: bool;
  /* _staleObservations: consecutive stale sightings per peer (GH-3604 hysteresis). */
  var staleCounts: map[int, int];
  var threshold: int;

  start state Booting {
    entry (cfg: (store: machine, k: int, threshold: int)) {
      store = cfg.store;
      k = cfg.k;
      threshold = cfg.threshold;
      budget = cfg.k;
      send store, eRegisterNode, this;
    }
    /* A fault cannot take effect before the row it acts on exists. */
    defer eStart;
    defer eCrash;
    defer eShutdown;
    on eNodeNumber do (m: (id: int)) {
      id = m.id;
      goto Waiting;
    }
  }

  state Waiting {
    entry {
      announceMe(false);
    }
    on eStart goto Ticking;
    on eRefill do { consumeRefill(); }
    on eCrash goto Dead;
    on eShutdown goto Stopping;
  }

  state Ticking {
    entry {
      announceMe(false);
      if (budget > 0) {
        budget = budget - 1;
        send store, eHeartbeat, (node = this, id = id, isLeader = isLeader);
        goto AwaitSnapshot;
      }
    }
    on eRefill do {
      consumeRefill();
      goto Ticking;
    }
    on eCrash goto Dead;
    on eShutdown goto Stopping;
  }

  state AwaitSnapshot {
    on eSnapshot do (m: (rows: map[int, tRow], youHoldLock: bool)) {
      var v: int;
      var newCounts: map[int, int];
      var doEject: bool;

      hasLock = m.youHoldLock;

      /* ejectStaleNodes: never self; hysteresis (eject only after `threshold` consecutive
         stale sightings, streak resets on a fresh read, GH-3604/D1); leader protection (a
         stale leader row may be destroyed only by the current lock holder). */
      foreach (v in keys(m.rows)) {
        if (v != id && m.rows[v].stale) {
          if (v in staleCounts) {
            newCounts[v] = staleCounts[v] + 1;
          } else {
            newCounts[v] = 1;
          }
          if (newCounts[v] >= threshold) {
            doEject = true;
            if (m.rows[v].leaderMark && !hasLock) {
              doEject = false;
            }
            if (doEject) {
              send store, eEject, (id = v,);
              newCounts -= (v);
            }
          }
        }
      }
      staleCounts = newCounts;

      /* A stale peer seen but not yet ejected is unfinished work — keep ticking until the
         cluster is clean, as the real loop would. See the README (PEx found this gap). */
      if (sizeof(newCounts) > 0 && budget < k) {
        budget = k;
      }

      /* GH-2602: we thought we were the leader, but the advisory lock is gone
         server-side. Step down cleanly, then fall through to the normal election
         attempt on this same tick. */
      if (isLeader && !hasLock) {
        stepDownLocally();
      }

      /* Always: an election attempt for a follower, a renewal for the leader. */
      send store, eTryAttain, (node = this, id = id);
      goto AwaitLock;
    }
    on eRefill do { consumeRefill(); }
    on eCrash goto Dead;
    on eShutdown goto Stopping;
  }

  state AwaitLock {
    on eLockResult do (m: (granted: bool)) {
      if (m.granted) {
        hasLock = true;
        if (!isLeader) {
          /* tryStartLeadershipAsync: persist the LeaderUri assignment, and only then
             act as the leader. */
          send store, eClaim, (node = this, id = id);
          goto AwaitClaim;
        }
        /* Already leader: the grant was a renewal; EvaluateAssignmentsAsync is out of
           this model's scope. */
        goto Ticking;
      }
      hasLock = false;
      if (isLeader) {
        /* The lock could not be renewed — someone else holds it. */
        stepDownLocally();
      }
      goto Ticking;
    }
    on eRefill do { consumeRefill(); }
    on eCrash goto Dead;
    on eShutdown goto Stopping;
  }

  state AwaitClaim {
    on eClaimAck do {
      isLeader = true;
      announceMe(true);
      goto Ticking;
    }
    on eRefill do { consumeRefill(); }
    on eCrash goto Dead;
    on eShutdown goto Stopping;
  }

  /* NodeAgentController.StopAsync: release the lock if believed held, delete own row. The
     process exit also closes the lock session (the reaper), freeing it even if the belief
     was stale. */
  state Stopping {
    entry {
      announce eMNodeState,
        (id = id, alive = true, isLeader = isLeader, busy = true, newFaults = 1);
      new SessionReaper((store = store, id = id, counted = true));
      send store, eReleaseAndDelete, (node = this, id = id, hadLock = hasLock);
    }
    on eStopAck goto Stopped;
    on eRefill do { announce eMRefillDone; }
    on eCrash do { }
    ignore eStart;
    ignore eSnapshot;
    ignore eLockResult;
    ignore eClaimAck;
  }

  state Stopped {
    entry {
      announceDead(0);
    }
    on eRefill do { announce eMRefillDone; }
    ignore eStart;
    ignore eCrash;
    ignore eShutdown;
    ignore eSnapshot;
    ignore eLockResult;
    ignore eClaimAck;
    ignore eStopAck;
  }

  /* A dirty crash — SIGKILL, OOM, a pulled node. No cleanup runs. The database frees the
     session's advisory lock and the row's heartbeat goes stale, each at a moment of the
     scheduler's choosing, and in either order. */
  state Dead {
    entry {
      announceDead(2);
      new SessionReaper((store = store, id = id, counted = true));
      new StaleReaper((store = store, id = id, counted = true));
    }
    on eRefill do { announce eMRefillDone; }
    ignore eStart;
    ignore eCrash;
    ignore eShutdown;
    ignore eSnapshot;
    ignore eLockResult;
    ignore eClaimAck;
    ignore eStopAck;
  }

  fun stepDownLocally() {
    isLeader = false;
    hasLock = false;
    send store, eStepDown, (node = this, id = id);
    announceMe(true);
  }

  fun consumeRefill() {
    if (budget < k) {
      budget = k;
    }
    /* Announce busy BEFORE signalling the refill consumed: eMRefillDone can drop the
       monitor's pendingRefills to zero, and busy=true must land first or the monitor reads
       a false quiescence in between. See the README (PEx found this in tcChaos). */
    announceMe(true);
    announce eMRefillDone;
  }

  fun announceMe(midTick: bool) {
    announce eMNodeState,
      (id = id, alive = true, isLeader = isLeader, busy = budget > 0 || midTick, newFaults = 0);
  }

  fun announceDead(faults: int) {
    announce eMNodeState,
      (id = id, alive = false, isLeader = false, busy = false, newFaults = faults);
  }
}
