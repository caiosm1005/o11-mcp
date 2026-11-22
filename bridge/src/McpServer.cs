using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ServiceStudioBridge;

/// <summary>
/// A minimal MCP server over Streamable HTTP: JSON-RPC requests in, JSON responses out,
/// no server-sent events. Built on HttpListener and System.Text.Json only, so Service
/// Studio's own web and MCP libraries can change version without breaking it.
/// </summary>
internal sealed class McpServer(int port, string token)
{
    private static readonly string[] SupportedProtocols = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly byte[] expectedAuthorization = Encoding.UTF8.GetBytes("Bearer " + token);

    public void Run()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Log.Info($"Listening on http://127.0.0.1:{port}/mcp");
        while (listener.IsListening)
        {
            var context = listener.GetContext();
            ThreadPool.QueueUserWorkItem(_ => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            HandleRequest(context);
        }
        catch (Exception ex)
        {
            Log.Error("A request failed", ex);
            try
            {
                Respond(context.Response, 500, null);
            }
            catch
            {
                // The client is gone.
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if ((request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty) != "/mcp")
        {
            Respond(response, 404, null);
            return;
        }

        // Browsers always send Origin and a local agent doesn't: refusing it keeps web pages
        // (and DNS-rebinding tricks) out. The Host check covers the rest of DNS rebinding.
        var host = request.UserHostName;
        if (!string.IsNullOrEmpty(request.Headers["Origin"]) || (host != $"127.0.0.1:{port}" && host != $"localhost:{port}"))
        {
            Respond(response, 403, null);
            return;
        }

        var authorization = Encoding.UTF8.GetBytes(request.Headers["Authorization"] ?? string.Empty);
        if (!CryptographicOperations.FixedTimeEquals(authorization, expectedAuthorization))
        {
            Respond(response, 401, null);
            return;
        }

        if (request.HttpMethod != "POST")
        {
            response.Headers["Allow"] = "POST";
            Respond(response, 405, null);
            return;
        }

        JsonNode? body;
        try
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            body = JsonNode.Parse(reader.ReadToEnd());
        }
        catch (Exception)
        {
            Respond(response, 400, Error(null, -32700, "Parse error"));
            return;
        }

        if (body is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var item in batch)
            {
                var reply = Dispatch(item as JsonObject);
                if (reply != null)
                {
                    replies.Add(reply);
                }
            }

            Respond(response, replies.Count == 0 ? 202 : 200, replies.Count == 0 ? null : replies);
            return;
        }

        var single = Dispatch(body as JsonObject);
        Respond(response, single == null ? 202 : 200, single);
    }

    /// <summary>Handles one JSON-RPC message; null for notifications and client responses, which get no reply.</summary>
    private static JsonObject? Dispatch(JsonObject? message)
    {
        if (message == null)
        {
            return Error(null, -32600, "Invalid request");
        }

        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>();
        if (method == null || id == null)
        {
            return null;
        }

        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(message["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = Tools.Definitions() },
                "tools/call" => Tools.Call(message["params"] as JsonObject),
                _ => throw new MissingMethodException(method),
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (MissingMethodException)
        {
            return Error(id, -32601, $"Method not found: {method}");
        }
        catch (ArgumentException ex)
        {
            return Error(id, -32602, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"{method} failed", ex);
            return Error(id, -32603, ex.Message);
        }
    }

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = requested != null && SupportedProtocols.Contains(requested) ? requested : SupportedProtocols[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "servicestudio-bridge", ["version"] = Bridge.Version },
            ["instructions"] =
                "Access to the OutSystems environment Service Studio is signed in to: applications, modules, " +
                "module versions, consumers, public elements, referenceable producers, and module downloads (OML files). " +
                "Every result names the environment it came from; call status to see it before anything else. " +
                "The bridge only fills gaps in Service Studio's own MCP server: when both offer the same thing, use Service Studio's.",
        };
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static void Respond(HttpListenerResponse response, int status, JsonNode? body)
    {
        response.StatusCode = status;
        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Write(body));
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes);
        }

        response.Close();
    }
}
