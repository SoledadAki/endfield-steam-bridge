using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace EndfieldSteamBridge
{
    // Use the Windows API directly: no schtasks parsing, temporary XML file,
    // or loss of the actual HRESULT behind a generic process exit code.
    internal static class ScheduledTasks
    {
        internal static void Register(string name, string xml)
        {
            WithFolder(name, "创建", delegate(object folder) {
                object task = Call(folder, "RegisterTask", name, xml, 6,
                    WindowsIdentity.GetCurrent().User.Value, null, 3, null);
                Release(task);
            });
        }
        internal static void Delete(string name)
        {
            WithFolder(name, "删除", delegate(object folder) {
                try { Call(folder, "DeleteTask", name, 0); }
                catch (Exception error) {
                    uint code = unchecked((uint)Unwrap(error).HResult);
                    if (code != 0x80070002 && code != 0x80070003) throw;
                }
            });
        }
        internal static void Run(string name)
        {
            WithFolder(name, "运行", delegate(object folder) {
                object task = null, running = null;
                try { task = Call(folder, "GetTask", name); running = Call(task, "Run", new object[] { null }); }
                finally { Release(running); Release(task); }
            });
        }
        private static object Call(object target, string method, params object[] args)
        { return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args); }
        private static void WithFolder(string name, string operation, Action<object> action)
        {
            object service = null, folder = null;
            try {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                Call(service, "Connect", null, null, null, null);
                folder = Call(service, "GetFolder", "\\");
                action(folder);
            }
            catch (Exception error) { throw new InvalidOperationException(Describe(operation, Unwrap(error)), Unwrap(error)); }
            finally { Release(folder); Release(service); }
        }
        private static Exception Unwrap(Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            return error;
        }
        private static void Release(object value)
        { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        internal static string Describe(string operation, Exception error)
        {
            uint code = unchecked((uint)error.HResult);
            string advice;
            switch (code) {
                case 0x80070005: advice = "Windows 拒绝了任务操作。请以管理员身份运行本程序，并检查安全软件是否拦截。"; break;
                case 0x80041315: case 0x80070422: case 0x800706BA:
                    advice = "Windows 计划任务服务不可用。请在“服务”中检查 Task Scheduler（任务计划程序）是否已启用并运行。"; break;
                case 0x80041310: case 0x80070534:
                    advice = "Windows 无法识别当前登录账号。请用安装 Steam 的 Windows 账号登录后重试。"; break;
                case 0x80040154:
                    advice = "Windows 计划任务组件未注册或缺失，需要修复系统组件后重试。"; break;
                default: advice = "请将此窗口完整截图反馈，包含下方版本和错误码。"; break;
            }
            return "无法" + operation + "辅助启动任务。\n\n错误码：0x" + code.ToString("X8") +
                "\n系统信息：" + error.Message.Trim() + "\n\n" + advice + "\n版本：" + App.Version;
        }
    }
}
