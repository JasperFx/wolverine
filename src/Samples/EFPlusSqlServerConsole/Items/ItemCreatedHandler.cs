using System;

namespace EFPlusSqlServerConsole.Items
{
    public class ItemCreatedHandler
    {
        public void Handle(ItemCreated @event)
        {
            Console.WriteLine("You created a new item with id " + @event.Id);
        }
    }
}
