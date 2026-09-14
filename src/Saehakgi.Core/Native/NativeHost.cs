using System.Text.Json.Nodes;

namespace Saehakgi.Core.Native;

/// <summary>
/// The native-messaging host logic that bridges the browser extension and the
/// encrypted cookie file. Handles one request at a time:
///   {"cmd":"ping"}
///   {"cmd":"put_cookies","path":..,"passphrase":..,"cookies":[...]}   (export)
///   {"cmd":"get_cookies","path":..,"passphrase":..}                    (import)
/// Responses are {"ok":true,...} or {"ok":false,"error":".."}.
/// </summary>
public static class NativeHost
{
    public const string Version = "0.1";

    /// <summary>Handles a single request JSON and returns the response JSON.</summary>
    public static string Handle(string requestJson)
    {
        try
        {
            var node = JsonNode.Parse(requestJson) ?? throw new InvalidDataException("empty request");
            var cmd = (string?)node["cmd"];
            switch (cmd)
            {
                case "ping":
                    return Ok(o => o["version"] = Version);

                case "put_cookies":
                {
                    var path = Required(node, "path");
                    var passphrase = Required(node, "passphrase");
                    var cookies = node["cookies"] as JsonArray ?? new JsonArray();
                    CookieStore.Save(path, cookies.ToJsonString(), passphrase);
                    return Ok(o => o["count"] = cookies.Count);
                }

                case "get_cookies":
                {
                    var path = Required(node, "path");
                    var passphrase = Required(node, "passphrase");
                    var cookiesJson = CookieStore.Load(path, passphrase);
                    var response = new JsonObject { ["ok"] = true, ["cookies"] = JsonNode.Parse(cookiesJson) };
                    return response.ToJsonString();
                }

                default:
                    return Err($"unknown cmd: {cmd}");
            }
        }
        catch (Exception ex)
        {
            return Err(ex.Message);
        }
    }

    /// <summary>Reads/handles/writes messages until the stream closes.</summary>
    public static void RunLoop(Stream input, Stream output)
    {
        while (true)
        {
            string? request;
            try { request = NativeMessaging.ReadMessage(input); }
            catch { break; }
            if (request is null) break;

            NativeMessaging.WriteMessage(output, Handle(request));
        }
    }

    private static string Required(JsonNode node, string name) =>
        (string?)node[name] ?? throw new InvalidDataException($"missing '{name}'");

    private static string Ok(Action<JsonObject> fill)
    {
        var o = new JsonObject { ["ok"] = true };
        fill(o);
        return o.ToJsonString();
    }

    private static string Err(string message) =>
        new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString();
}
