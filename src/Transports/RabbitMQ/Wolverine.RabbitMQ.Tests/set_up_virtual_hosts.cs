using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public static class Base64Url
{
    public static string Encode(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-')
            .Replace('/', '_');
    }

    public static string Decode(string text)
    {
        text = text.Replace('_', '/').Replace('-', '+');
        switch (text.Length % 4)
        {
            case 2:
                text += "==";
                break;
            case 3:
                text += "=";
                break;
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(text));
    }
}

public class set_up_virtual_hosts
{
    /*
        jeremymiller@CritterMaster wolverine % curl -u guest:guest -X PUT http://localhost:15672/api/vhosts/vh1
       jeremymiller@CritterMaster wolverine % curl -u guest:guest -X PUT http://localhost:15672/api/vhosts/vh2
       jeremymiller@CritterMaster wolverine % curl -u guest:guest -X PUT http://localhost:15672/api/vhosts/vh3
     */
    
    
    [Fact]
    public async Task try_it_out()
    {
        using var client = new HttpClient();
        var credentials = Base64Url.Encode("guest:guest");

        var request = new HttpRequestMessage(HttpMethod.Put, "http://localhost:15672/api/vhosts/vh1");
        request.Headers.Add("authorization", credentials);
        var response = await client.SendAsync(request);
        
        Debug.WriteLine(response);
    }
}