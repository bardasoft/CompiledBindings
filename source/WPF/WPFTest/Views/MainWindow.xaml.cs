﻿using System.Windows;
using WPFTest.ViewModels;

namespace WPFTest.Views
{
	public partial class MainWindow : Window
	{
		MainViewModel _viewModel;

		public MainWindow()
		{
			InitializeComponent();

			DataContext = _viewModel = new MainViewModel(); System.Windows.Media.Brush

        }

		public string InstanceFunction(int val1, int val2) => $"InstanceFunction({val1}, {val2})";
    }
}

namespace WPFTest
{
	static partial class UIElementExtensions
	{
		public static void SetVisible(this UIElement element, bool isVisible) { }
	}
}