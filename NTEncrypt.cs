using System;
using System.Reflection;

class NTEncrypt
{
    static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: NTEncrypt.exe <encrypt|decrypt> <text>");
            Environment.Exit(1);
        }

        var asm = Assembly.LoadFrom(@"C:\Program Files\NinjaTrader 8\bin\NinjaTrader.Core.dll");
        var globalsType = asm.GetType("NinjaTrader.Core.Globals");
        var authType = asm.GetType("NinjaTrader.Core.Authentication");

        var keyField = authType.GetField("key", BindingFlags.Static | BindingFlags.NonPublic);
        string key = (string)keyField.GetValue(null);

        string methodName = args[0] == "encrypt" ? "Encrypt" : "Decrypt";
        var method = globalsType.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public);
        string result = (string)method.Invoke(null, new object[] { args[1], key });

        Console.Write(result);
    }
}
