using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TestingSupport.ErrorHandling;

public class ErrorCausingMessage
{
    [JsonIgnore]
    public Dictionary<int, Exception> Errors { get; set; } = new();
    public bool WasProcessed { get; set; }
    public int LastAttempt { get; set; }
}