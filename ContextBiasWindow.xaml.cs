using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace WhisperInk
{
    public partial class ContextBiasWindow : Window
    {
        public List<string> BiasTerms { get; private set; } = new();
        
        public ContextBiasWindow(List<string> initialTerms)
        {
            InitializeComponent();
            
            // Load existing terms into text box
            TxtBiasTerms.Text = string.Join("\n", initialTerms);
        }
        
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Parse terms from text box (one per line)
            BiasTerms = TxtBiasTerms.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();
            
            DialogResult = true;
            Close();
        }
        
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
