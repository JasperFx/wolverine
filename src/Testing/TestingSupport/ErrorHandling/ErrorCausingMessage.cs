using System;
using System.Collections.Generic;

namespace TestingSupport.ErrorHandling
{
    public class ErrorCausingMessage
    {
        public Dictionary<int, Exception> Errors { get; set; } = new Dictionary<int, Exception>();
        public bool WasProcessed { get; set; }
        public int LastAttempt { get; set; }
    }
}
