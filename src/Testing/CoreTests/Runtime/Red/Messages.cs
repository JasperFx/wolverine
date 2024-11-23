namespace CoreTests.Runtime.Red;

[Red]
public class RedMessage1;

[Crimson]
public class RedMessage2;

[Burgundy]
public class RedMessage3;

public class RedAttribute : Attribute;

public class CrimsonAttribute : RedAttribute;

public class BurgundyAttribute : RedAttribute;
