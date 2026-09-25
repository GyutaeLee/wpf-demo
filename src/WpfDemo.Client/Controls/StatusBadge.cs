using System.Windows;
using System.Windows.Controls;

namespace WpfDemo.Controls
{
    public sealed class StatusBadge : Control
    {
        static StatusBadge()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusBadge),
                new FrameworkPropertyMetadata(typeof(StatusBadge)));
        }

        public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
            nameof(Status), typeof(string), typeof(StatusBadge),
            new FrameworkPropertyMetadata(WorkStatus.Waiting));

        public string Status
        {
            get { return (string)GetValue(StatusProperty); }
            set { SetValue(StatusProperty, value); }
        }
    }
}
