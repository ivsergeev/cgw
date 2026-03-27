using System;
using System.Collections.Generic;

namespace CorpGateway.Models;

public class DomainAuthData
{
    public string Domain { get; set; } = "";
    public Dictionary<string, string> Cookies { get; set; } = new();
    public Dictionary<string, string> AuthHeaders { get; set; } = new();
    public DateTime FetchedAt { get; set; }
}
