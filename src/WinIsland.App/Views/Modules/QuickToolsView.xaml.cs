using System.Windows.Controls;
using System.Windows.Input;
using WinIsland.App.Services;
using WinIsland.App.ViewModels;

namespace WinIsland.App.Views.Modules
{
    public partial class QuickToolsView : UserControl
    {
        public QuickToolsView()
        {
            InitializeComponent();
        }

        private void OnClipboardItemClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is ClipboardItem item && DataContext is MainIslandViewModel vm)
            {
                vm.CopyClipboardItemCommand.Execute(item);
            }
        }
    }
}
