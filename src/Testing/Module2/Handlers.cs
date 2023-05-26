namespace Module2;

public record Module2Message1;
public record Module2Message2;
public record Module2Message3;
public record Module2Message4;

public class MessageHandler
{
    public void Handle(Module2Message1 message){}
    public void Handle(Module2Message2 message){}
    public void Handle(Module2Message3 message){}
    public void Handle(Module2Message4 message){}
}