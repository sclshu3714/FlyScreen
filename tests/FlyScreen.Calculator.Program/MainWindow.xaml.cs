using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyScreen.ProgramDevHost {
    public partial class MainWindow : Window {
        readonly CalculatorSession _calc = new();

        public MainWindow() {
            InitializeComponent();
            Loaded += OnLoaded;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        void OnLoaded(object sender, RoutedEventArgs e) {
            AttachButtons(this);
            ApplyText();
            Focus();
        }

        void AttachButtons(DependencyObject node) {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is Button button && button.Tag is string) {
                    button.Click += OnCalcButton;
                }
                AttachButtons(child);
            }
        }

        void OnCalcButton(object sender, RoutedEventArgs e) {
            if (sender is not Button button || button.Tag is not string tag || tag.Length == 0) return;
            _calc.Input(tag);
            ApplyText();
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e) {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            string tag = CalculatorSession.TagFromKey(e.Key.ToString(), shift);
            if (tag.Length == 0) return;
            _calc.Input(tag);
            ApplyText();
            e.Handled = true;
        }

        void ApplyText() {
            ExprText.Text = _calc.ExpressionText;
            ResultText.Text = _calc.ResultText;
        }
    }
}
