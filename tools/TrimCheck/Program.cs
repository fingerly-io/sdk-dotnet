// Touches every code path that serializes or deserializes, so the trimmer has
// something to complain about if one of them stopped using the source
// generator. It is never expected to reach a network.
using System;
using System.IO;
using System.Text;
using Fingerly;

using var fingerly = new FingerlyClient(new FingerlyOptions
{
    SecretKey = "fly_sk_production_trimcheck",
    Endpoint = "http://127.0.0.1:9",
    Timeout = TimeSpan.FromMilliseconds(250),
});

await Expect<FingerlyException>(() => fingerly.Events.GetAsync("r1"));
await Expect<FingerlyException>(
    () => fingerly.Events.ListAsync(new EventListOptions { Level = "high", Limit = 5 }));

Console.WriteLine(Webhooks.Verify(
    "whsec", Encoding.UTF8.GetBytes("{}"), "1800000000", "sha256=nope"));
Console.WriteLine(Webhooks.Parse(
    Encoding.UTF8.GetBytes("""{"id":"evt_1","type":"t","data":{}}""")).Id);

using var proxy = new FingerlyProxy(new FingerlyProxyOptions
{
    ProxyKey = "fly_px_production_trimcheck",
    Path = "/k7x2qm4a",
    Upstream = "http://127.0.0.1:9",
    ClientIp = _ => "203.0.113.7",
});
var request = new ProxyRequest("POST", "/k7x2qm4a")
{
    Body = new MemoryStream(Encoding.UTF8.GetBytes("{}")),
};
request.AddHeader("X-API-Key", "fly_pk_production_trimcheck");
Console.WriteLine((await proxy.HandleAsync(request)).Status);

Console.WriteLine("trim check ok");

static async System.Threading.Tasks.Task Expect<T>(Func<System.Threading.Tasks.Task> call)
    where T : Exception
{
    try
    {
        await call();
    }
    catch (T failure)
    {
        Console.WriteLine(failure.GetType().Name);
        return;
    }
    throw new InvalidOperationException("expected " + typeof(T).Name);
}
