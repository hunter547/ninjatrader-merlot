using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

class AutoLogin
{
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: AutoLogin.exe <username> <password>");
            Environment.Exit(1);
        }

        string username = args[0];
        string password = args[1];

        Console.WriteLine("Waiting for NinjaTrader login window...");

        // Wait up to 60 seconds for the Welcome window
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 60; i++)
        {
            hwnd = FindWindow(null, "Welcome");
            if (hwnd != IntPtr.Zero) break;
            Thread.Sleep(1000);
        }

        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("ERROR: Welcome window not found after 60s");
            Environment.Exit(1);
        }

        Console.WriteLine("Found Welcome window");
        SetForegroundWindow(hwnd);
        Thread.Sleep(500);

        AutomationElement window = AutomationElement.FromHandle(hwnd);

        // Check if this is the login screen or the trading mode screen
        AutomationElement loginBtn = window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "btnLogin"));

        if (loginBtn != null)
        {
            // --- Login screen ---
            Console.WriteLine("Login screen detected");

            // Set username
            AutomationElement usernameBox = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "tbUserName"));

            if (usernameBox != null)
            {
                object valPattern;
                if (usernameBox.TryGetCurrentPattern(ValuePattern.Pattern, out valPattern))
                {
                    string currentValue = ((ValuePattern)valPattern).Current.Value;
                    if (string.IsNullOrEmpty(currentValue))
                    {
                        ((ValuePattern)valPattern).SetValue(username);
                        Console.WriteLine("Username was empty — set to provided value");
                    }
                    else
                    {
                        Console.WriteLine("Username already populated: " + currentValue);
                    }
                }
            }

            Thread.Sleep(200);

            // Set password
            AutomationElement passwordBox = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "passwordBox"));

            if (passwordBox != null)
            {
                object valPattern;
                if (passwordBox.TryGetCurrentPattern(ValuePattern.Pattern, out valPattern))
                {
                    ((ValuePattern)valPattern).SetValue(password);
                    Console.WriteLine("Password set");
                }
            }

            Thread.Sleep(200);

            // Click Login
            object invokePattern;
            if (loginBtn.TryGetCurrentPattern(InvokePattern.Pattern, out invokePattern))
            {
                ((InvokePattern)invokePattern).Invoke();
                Console.WriteLine("Login clicked");
            }

            // Wait for trading mode screen to appear (up to 30 seconds)
            Console.WriteLine("Waiting for trading mode screen...");
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(1000);
                hwnd = FindWindow(null, "Welcome");
                if (hwnd == IntPtr.Zero)
                {
                    Console.WriteLine("Welcome window closed — NinjaTrader loaded directly");
                    return;
                }

                window = AutomationElement.FromHandle(hwnd);
                AutomationElement launchBtn = window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "btnSimulation"));

                if (launchBtn != null)
                {
                    ClickLaunch(launchBtn);
                    return;
                }
            }

            Console.WriteLine("WARNING: Trading mode screen did not appear after 30s");
        }
        else
        {
            // --- Trading mode screen (already logged in) ---
            AutomationElement launchBtn = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "btnSimulation"));

            if (launchBtn != null)
            {
                ClickLaunch(launchBtn);
            }
            else
            {
                Console.WriteLine("WARNING: Neither login nor launch button found");
            }
        }
    }

    static void ClickLaunch(AutomationElement launchBtn)
    {
        Console.WriteLine("Trading mode screen detected — clicking Launch");
        object invokePattern;
        if (launchBtn.TryGetCurrentPattern(InvokePattern.Pattern, out invokePattern))
        {
            ((InvokePattern)invokePattern).Invoke();
            Console.WriteLine("Launch clicked");
        }
    }
}
