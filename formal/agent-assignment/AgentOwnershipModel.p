/*
Single-agent ownership across a partition heal. See formal/agent-assignment/README.md for
the model, its scope, and the mutant ledger.

Store = node_assignments rows + live set + the leader's placement, serialized.
Node  = one process: its health-check tick and the local fact of running the agent.
Leadership correctness is assumed (proved by the leader-election spec); here it can move.
*/

event eJoin: (node: machine, id: int);   // id assigned at construction, self-reported
event eJoined;
event eGo;

/* A health-check tick's round trip. */
event eSync: (node: machine, id: int);
event eSyncReply: (rows: map[int, bool], live: map[int, bool], amLeader: bool);

/* A node writes its OWN row after it starts the agent and removes it after it stops. */
event eAddRow: (id: int);                 // AddAssignmentAsync (start / resurrection restore)
event eRemoveRow: (id: int);              // RemoveAssignmentAsync (stop)
/* Leader -> store placement decisions. */
event eAssignReq: (id: int);              // AssignAgent
event eStopReq: (id: int);                // StopRemoteAgent
event eRun: (run: bool);                  // store -> node, over a courier so it interleaves

/* Faults. */
event eCutoff;                            // partition, node side: unreachable, keeps running
event eEjectMember: (id: int, counted: bool);  // eject row + liveness; counted closes a crash fault
event eReconnect;                         // partition heals
event eCrash;                             // permanent, dirty departure
event eRefill;                            // stand-in for "the health-check loop runs forever"

/* An arsonist ARMS the store; the store fires the strike on the next node to become owner,
   so the fault lands on a running node. See the README on why a fixed up-front target
   cannot reach the interesting case. */
event eArm: (kind: int);                  // 0 = partition/heal, 1 = crash
event ePartitionNode: (id: int);
event eCrashNode: (id: int);

/* rows[id] = holds a durable assignment row; live[id] = store considers id reachable. */
machine Store {
  var rows: map[int, bool];
  var live: map[int, bool];
  var members: map[int, machine];
  var leader: int;
  var armedPartitions: int;
  var armedCrashes: int;

  start state Serving {
    on eArm do (m: (kind: int)) {
      if (m.kind == 0) { armedPartitions = armedPartitions + 1; }
      else { armedCrashes = armedCrashes + 1; }
    }

    on eJoin do (m: (node: machine, id: int)) {
      members[m.id] = m.node;
      live[m.id] = true;
      send m.node, eJoined;
      announceStore(0, 0);
    }

    on eSync do (m: (node: machine, id: int)) {
      var refills: int;
      /* A syncing node is reachable; a heal (was not live) refills the fleet. */
      if (m.id in live && live[m.id]) {
        refills = 0;
      } else {
        live[m.id] = true;
        refills = refillAll();
      }
      /* Leadership handover (assumed correct): if no live leader, the syncer takes it. */
      if (leader == 0 || !(leader in live) || !live[leader]) {
        leader = m.id;
      }
      send m.node, eSyncReply, (rows = rows, live = live, amLeader = leader == m.id);
      announceStore(0, refills);
    }

    /* ensureLocalNodeRegisteredAsync + AssignAgentsAsync (GH-3604/D2). Only a live node's
       row is honored. A durable change refills the fleet so the leader re-evaluates. */
    on eAddRow do (m: (id: int)) {
      var refills: int;
      if ((m.id in live) && live[m.id] && !(m.id in rows && rows[m.id])) {
        rows[m.id] = true;
        refills = refillAll();
        /* Fire one armed fault on the new owner (partition before crash). */
        if (armedPartitions > 0) {
          armedPartitions = armedPartitions - 1;
          new StrikeCourier((store = this, id = m.id, kind = 0));
        } else if (armedCrashes > 0) {
          armedCrashes = armedCrashes - 1;
          new StrikeCourier((store = this, id = m.id, kind = 1));
        }
      }
      announceRowDone(refills);
    }

    on eRemoveRow do (m: (id: int)) {
      var refills: int;
      if (m.id in rows && rows[m.id]) {
        rows[m.id] = false;
        refills = refillAll();
      }
      announceRowDone(refills);
    }

    on eAssignReq do (m: (id: int)) {
      new RunCourier((target = members[m.id], run = true));
      announceStore(1, 0);
    }

    on eStopReq do (m: (id: int)) {
      new RunCourier((target = members[m.id], run = false));
      announceStore(1, 0);
    }

    /* Partition: cut the owner off (it keeps running) and eject it store-side. The fault
       opened here is closed when the node heals or dies. */
    on ePartitionNode do (m: (id: int)) {
      var refills: int;
      announce eMFault, (delta = 1,);
      send members[m.id], eCutoff;
      live[m.id] = false;
      rows[m.id] = false;
      refills = refillAll();
      announceStore(0, refills);
    }

    on eCrashNode do (m: (id: int)) {
      send members[m.id], eCrash;
    }

    /* Eject a node's row and liveness. counted closes the crash fault Node.Dead opened. */
    on eEjectMember do (m: (id: int, counted: bool)) {
      var refills: int;
      live[m.id] = false;
      rows[m.id] = false;
      refills = refillAll();
      announceStore(0, refills);
      if (m.counted) {
        announce eMFault, (delta = -1,);
      }
    }
  }

  fun refillAll(): int {
    var i: int;
    var n: int;
    foreach (i in keys(members)) {
      if (live[i]) {
        send members[i], eRefill;
        n = n + 1;
      }
    }
    return n;
  }

  fun announceStore(newRuns: int, newRefills: int) {
    announce eMStore, (leader = leader, rows = rows, live = live, newRuns = newRuns, newRefills = newRefills);
  }

  fun announceRowDone(newRefills: int) {
    announce eMStore, (leader = leader, rows = rows, live = live, newRuns = 0, newRefills = newRefills);
    announce eMRowDone;
  }
}

/* Each response/fault rides its own courier so it interleaves with the ticks. */
machine RunCourier {
  start state Deliver {
    entry (m: (target: machine, run: bool)) {
      send m.target, eRun, (run = m.run,);
    }
  }
}

machine HealCourier {
  start state Deliver {
    entry (node: machine) {
      send node, eReconnect;
    }
  }
}

machine Arsonist {
  start state Init {
    entry (cfg: (store: machine, kind: int)) {
      send cfg.store, eArm, (kind = cfg.kind,);
    }
  }
}

machine StrikeCourier {
  start state Deliver {
    entry (m: (store: machine, id: int, kind: int)) {
      if (m.kind == 0) {
        send m.store, ePartitionNode, (id = m.id,);
      } else {
        send m.store, eCrashNode, (id = m.id,);
      }
    }
  }
}

/* localRun is the local fact of running the agent; it survives a partition, because a
   cut-off node cannot be told to stop. The leader's placement logic runs in the tick. */
machine Node {
  var store: machine;
  var id: int;
  var k: int;
  var budget: int;
  var localRun: bool;
  /* Owes the monitor a fault-close (paired with the +1 the partition opened); closed on
     heal or death so a partitioned-then-crashed node cannot leak an open fault. */
  var owesFaultClose: bool;

  start state Booting {
    entry (cfg: (store: machine, id: int, k: int)) {
      store = cfg.store;
      id = cfg.id;
      k = cfg.k;
      budget = cfg.k;
      send store, eJoin, (node = this, id = id);
    }
    defer eGo;
    defer eCutoff;
    defer eCrash;
    on eJoined goto Idle;
  }

  state Idle {
    /* Busy until eGo: full budget, about to start — not quiescent before first placement. */
    entry { announceMe(true); }
    on eGo goto Ticking;
    on eRefill do { consumeRefill(); }
    on eRun do (m: (run: bool)) { applyRun(m.run); }
    on eCutoff goto Partitioned;
    on eCrash goto Dead;
    /* A sync reply can land after a cutoff+reconnect. */
    ignore eSyncReply;
  }

  state Ticking {
    entry {
      if (budget > 0) {
        announceMe(true);
        budget = budget - 1;
        send store, eSync, (node = this, id = id);
        goto AwaitSync;
      }
      announceMe(false);
    }
    on eRefill do { consumeRefill(); goto Ticking; }
    on eRun do (m: (run: bool)) { applyRun(m.run); }
    on eCutoff goto Partitioned;
    on eCrash goto Dead;
    ignore eSyncReply;
  }

  state AwaitSync {
    on eSyncReply do (r: (rows: map[int, bool], live: map[int, bool], amLeader: bool)) {
      var owners: seq[int];
      var i: int;
      var keep: int;
      var target: int;
      var work: bool;

      /* Resurrection (GH-3604/D2): restore my row if it went missing while I'm running.
         This is what surfaces a post-heal duplicate for the healer to see. */
      if (localRun && (!(id in r.rows) || !r.rows[id])) {
        announce eMRowPending;
        send store, eAddRow, (id = id,);
      }

      if (r.amLeader) {
        /* Live row-holders: the copies the leader can see. */
        foreach (i in keys(r.rows)) {
          if (r.rows[i] && (i in r.live) && r.live[i]) {
            owners += (sizeof(owners), i);
          }
        }

        if (sizeof(owners) == 0) {
          /* Place it on the lowest live id — deterministic, so a start in flight is
             re-dispatched to the same node rather than spawning a rival copy. */
          target = lowestLive(r.live);
          if (target != 0) {
            send store, eAssignReq, (id = target,);
            work = true;
          }
        } else if (sizeof(owners) >= 2) {
          /* GH-2602 healer: keep one copy (highest id, arbitrary), stop the rest. */
          keep = owners[0];
          i = 1;
          while (i < sizeof(owners)) {
            if (owners[i] > keep) { keep = owners[i]; }
            i = i + 1;
          }
          i = 0;
          while (i < sizeof(owners)) {
            if (owners[i] != keep) {
              send store, eStopReq, (id = owners[i],);
            }
            i = i + 1;
          }
          work = true;
        }

        /* Keep ticking while there is placement work outstanding. */
        if (work && budget < k) {
          budget = k;
        }
      }

      goto Ticking;
    }
    on eRefill do { consumeRefill(); }
    on eRun do (m: (run: bool)) { applyRun(m.run); }
    on eCutoff goto Partitioned;
    on eCrash goto Dead;
  }

  /* Cut off but still running; row/liveness ejected store-side. The partition is temporary. */
  state Partitioned {
    entry {
      owesFaultClose = true;
      announcePartitioned();
      new HealCourier(this);
    }
    /* Heal: close the fault and take a fresh budget so the node syncs and restores its row. */
    on eReconnect do {
      closeFaultIfOwed();
      budget = k;
      goto Ticking;
    }
    /* A command lost to an unreachable node still retires its courier's accounting. */
    on eRun do (m: (run: bool)) { announce eMRunDone; }
    on eRefill do { announce eMRefillDone; }
    on eCrash goto Dead;
    ignore eGo;
    ignore eSyncReply;
    ignore eCutoff;
  }

  state Dead {
    entry {
      localRun = false;
      /* Open the crash fault BEFORE announcing the death: announceDead drops the runner
         count to zero, and the monitor must not read that as quiescence before recovery.
         Closed by the store's eject below. */
      announce eMFault, (delta = 1,);
      closeFaultIfOwed();
      announceDead();
      send store, eEjectMember, (id = id, counted = true);
    }
    on eRun do (m: (run: bool)) { announce eMRunDone; }
    on eRefill do { announce eMRefillDone; }
    ignore eGo;
    ignore eSyncReply;
    ignore eCutoff;
    ignore eReconnect;
    ignore eCrash;
  }

  fun applyRun(run: bool) {
    localRun = run;
    /* Write my own durable row to match, so a row never outlives its runner. */
    announce eMRowPending;
    if (run) {
      send store, eAddRow, (id = id,);
    } else {
      send store, eRemoveRow, (id = id,);
    }
    announceMe(budget > 0);
    announce eMRunDone;
  }

  fun closeFaultIfOwed() {
    if (owesFaultClose) {
      owesFaultClose = false;
      announce eMFault, (delta = -1,);
    }
  }

  fun consumeRefill() {
    if (budget < k) { budget = k; }
    /* Announce busy before signalling the refill consumed (see leader-election). */
    announceMe(true);
    announce eMRefillDone;
  }

  fun lowestLive(live: map[int, bool]): int {
    var i: int;
    var best: int;
    best = 0;
    foreach (i in keys(live)) {
      if (live[i] && (best == 0 || i < best)) {
        best = i;
      }
    }
    return best;
  }

  fun announceMe(busy: bool) {
    announce eMNode, (id = id, localRun = localRun, partitioned = false, alive = true, busy = busy);
  }

  fun announcePartitioned() {
    announce eMNode, (id = id, localRun = localRun, partitioned = true, alive = true, busy = true);
  }

  fun announceDead() {
    announce eMNode, (id = id, localRun = false, partitioned = false, alive = false, busy = false);
  }
}
