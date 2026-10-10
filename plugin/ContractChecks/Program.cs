using JellyCache.Plugin;

var checks = 0;
void Check(bool ok) { if (!ok) throw new Exception("Contract check failed"); checks++; }
void Reject(Action action) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception("Expected rejection"); }
Check(BridgeProtocol.RelativePath("/video/show/02.mkv", "/video") == "show/02.mkv");
Reject(() => BridgeProtocol.RelativePath("/video-other/02.mkv", "/video"));
Reject(() => BridgeProtocol.RelativePath("/video/../secret", "/video"));
Reject(() => BridgeProtocol.RelativePath("/video/show\\secret", "/video"));
Check(BridgeProtocol.Endpoint("http://127.0.0.1:8765/v1/events").Port == 8765);
Reject(() => BridgeProtocol.Endpoint("http://example.com/v1/events"));
Reject(() => BridgeProtocol.Endpoint("http://127.0.0.1:8765/v1/events?token=x"));
var buffer = new EventBuffer<int>();
Check(buffer.Put("session", "old", "start", 1));
Check(buffer.Put("session", "old", "progress", 2));
Check(buffer.Put("session", "new", "start", 3));
Check(!buffer.Put("session", "old", "stop", 4));
Check(!buffer.Put("session", "old", "progress", 5));
var first = buffer.Drain().Single();
Check(first.Value == 3 && first.Phase == "start");
Check(buffer.Put("session", "new", "pause", 6));
var second = buffer.Drain().Single();
Check(second.Sequence > first.Sequence && second.Phase == "pause");
Check(buffer.Put("session", "new", "stop", 7));
Check(buffer.Drain().Single().Phase == "stop");
Check(buffer.Drain().Length == 0);
for (var i = 1; i < 128; i++) Check(buffer.Put("s" + i, "p", "start", i));
Check(!buffer.Put("overflow", "p", "start", 0));
Console.WriteLine($"{checks} contract assertions passed");
