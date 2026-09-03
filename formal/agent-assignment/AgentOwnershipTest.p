/*
The cases (see the README): steady (no fault), partition (owner cut off, then heals),
crash (owner dies permanently), chaos (both). Faults strike the current owner via an
Arsonist — see the README on why a fixed up-front target misses the interesting case.
*/

machine SteadyDriver {
  start state Init { entry { begin(3, 0, 0); } }
}

machine PartitionDriver {
  start state Init { entry { begin(3, 1, 0); } }
}

machine CrashDriver {
  start state Init { entry { begin(3, 0, 1); } }
}

machine ChaosDriver {
  start state Init { entry { begin(3, 1, 1); } }
}

fun begin(n: int, partitions: int, crashes: int) {
  var store: machine;
  var nodes: seq[machine];
  var i: int;

  announce eMRoster, (count = n,);

  store = new Store();
  i = 0;
  while (i < n) {
    nodes += (i, new Node((store = store, id = i + 1, k = 3)));
    i = i + 1;
  }
  i = 0;
  while (i < n) {
    send nodes[i], eGo;
    i = i + 1;
  }

  /* Arsonists strike the current owner; kind 0 = partition/heal, kind 1 = crash. */
  i = 0;
  while (i < partitions) {
    new Arsonist((store = store, kind = 0));
    i = i + 1;
  }
  i = 0;
  while (i < crashes) {
    new Arsonist((store = store, kind = 1));
    i = i + 1;
  }
}

test tcSteadyState [main=SteadyDriver]:
  assert OwnershipConverges in
    { SteadyDriver, Store, Node, RunCourier, HealCourier, Arsonist, StrikeCourier };

test tcPartitionHeal [main=PartitionDriver]:
  assert OwnershipConverges in
    { PartitionDriver, Store, Node, RunCourier, HealCourier, Arsonist, StrikeCourier };

test tcCrash [main=CrashDriver]:
  assert OwnershipConverges in
    { CrashDriver, Store, Node, RunCourier, HealCourier, Arsonist, StrikeCourier };

test tcChaos [main=ChaosDriver]:
  assert OwnershipConverges in
    { ChaosDriver, Store, Node, RunCourier, HealCourier, Arsonist, StrikeCourier };
