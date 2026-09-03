/*
The cases (see the README): quiet (no fault), crash (dirty death), stop (graceful
shutdown), drop (lock session dies under a live node, GH-2602), blip (heartbeat goes stale,
GH-3604), chaos (all of the above). Faults are scheduler-targeted; each node gets `k` ticks
and every fault refunds them, so quiescence is always reachable after the last fault.
*/

machine QuietDriver {
  start state Init {
    entry {
      begin(3, false, false, 0, 0);
    }
  }
}

machine CrashDriver {
  start state Init {
    entry {
      begin(3, true, false, 0, 0);
    }
  }
}

machine StopDriver {
  start state Init {
    entry {
      begin(3, false, true, 0, 0);
    }
  }
}

machine DropDriver {
  start state Init {
    entry {
      begin(3, false, false, 1, 0);
    }
  }
}

machine BlipDriver {
  start state Init {
    entry {
      begin(3, false, false, 0, 2);
    }
  }
}

machine ChaosDriver {
  start state Init {
    entry {
      begin(3, true, true, 1, 1);
    }
  }
}

/* Stand up the store and the nodes, start them ticking, then loose the faults. Faults
   ride couriers so their arrival interleaves freely with the ticks — a fault can land
   before the first election, in the middle of one, or long after it settled. */
fun begin(n: int, crashOne: bool, stopOne: bool, drops: int, blips: int) {
  var store: machine;
  var nodes: seq[machine];
  var i: int;

  announce eMRoster, (count = n,);

  store = new Store();
  i = 0;
  while (i < n) {
    nodes += (i, new Node((store = store, k = 3, threshold = 2)));
    i = i + 1;
  }
  i = 0;
  while (i < n) {
    send nodes[i], eStart;
    i = i + 1;
  }

  if (crashOne) {
    new CrashCourier(nodes[choose(n)]);
  }
  if (stopOne) {
    new ShutdownCourier(nodes[choose(n)]);
  }
  i = 0;
  while (i < drops) {
    new SessionReaper((store = store, id = choose(n) + 1, counted = false));
    i = i + 1;
  }
  i = 0;
  while (i < blips) {
    new StaleReaper((store = store, id = choose(n) + 1, counted = false));
    i = i + 1;
  }
}

test tcQuietCluster [main=QuietDriver]:
  assert ElectionConverges in
    { QuietDriver, Store, Node, SessionReaper, StaleReaper, CrashCourier, ShutdownCourier };

test tcCrash [main=CrashDriver]:
  assert ElectionConverges in
    { CrashDriver, Store, Node, SessionReaper, StaleReaper, CrashCourier, ShutdownCourier };

test tcGracefulStop [main=StopDriver]:
  assert ElectionConverges in
    { StopDriver, Store, Node, SessionReaper, StaleReaper, CrashCourier, ShutdownCourier };

test tcLockSessionDrop [main=DropDriver]:
  assert ElectionConverges in
    { DropDriver, Store, Node, SessionReaper, StaleReaper, CrashCourier, ShutdownCourier };

test tcHeartbeatBlip [main=BlipDriver]:
  assert ElectionConverges in
    { BlipDriver, Store, Node, SessionReaper, StaleReaper, CrashCourier, ShutdownCourier };

test tcChaos [main=ChaosDriver]:
  assert ElectionConverges in
    { ChaosDriver, Store, Node, SessionReaper, StaleReaper, CrashCourier, ShutdownCourier };
