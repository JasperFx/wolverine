namespace Wolverine.Configuration
{
    public enum RoutingMode
    {
        /// <summary>
        /// The endpoint is a static address when sending messages
        /// </summary>
        Static,
        
        /// <summary>
        /// The endpoint uses topic-based routing when sending messages
        /// </summary>
        ByTopic
    }
}
