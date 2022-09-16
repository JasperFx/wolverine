using System;
using Oakton;

namespace Module1
{
    public class TalkCommand : OaktonCommand<NetCoreInput>
    {
        public override bool Execute(NetCoreInput input)
        {
            ConsoleWriter.Write(ConsoleColor.Magenta, "Hello!");
            return true;
        }
    }
}
