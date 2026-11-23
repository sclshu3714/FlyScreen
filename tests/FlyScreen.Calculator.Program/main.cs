// namespace: com.xuanniao.test
// name: test
using FlyScreen.ProgramDevHost;

Console.WriteLine("Hello from jishuanqi");

var result = await UiHost.ShowMainWindowAsync(ct);
Console.WriteLine($"Result = {result}");
