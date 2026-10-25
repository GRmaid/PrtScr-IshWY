using System.Windows.Input;

namespace PrtScr.App.Services;

/// <summary>极简命令，用于托盘图标等只接受 ICommand 的挂点。</summary>
public sealed class DelegateCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
