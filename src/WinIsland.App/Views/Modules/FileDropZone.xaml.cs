using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinIsland.App.Services;

namespace WinIsland.App.Views.Modules
{
    public partial class FileDropZone : UserControl
    {
        private Point _dragStartPoint;
        private bool _isDraggingCard;

        public FileDropZone()
        {
            InitializeComponent();
        }

        private void OnFileDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void OnFileDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void OnFileDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    FileShelfService.Instance.AddFiles(files);
                }
            }
            e.Handled = true;
        }

        private void OnFileCardMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(this);
            _isDraggingCard = true;
        }

        private void OnFileCardMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingCard || e.LeftButton != MouseButtonState.Pressed)
            {
                _isDraggingCard = false;
                return;
            }

            Point currentPoint = e.GetPosition(this);
            Vector diff = _dragStartPoint - currentPoint;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _isDraggingCard = false;
                if (sender is FrameworkElement elem && elem.DataContext is FileShelfItem item)
                {
                    FileShelfService.Instance.StartDragOut(elem, item);
                }
            }
        }
    }
}
