using System.Diagnostics;
using System.Windows.Input;
using ArcGIS.Desktop.Framework;
using GoBase.Pages.Base;
using GoBase.Services;

namespace GoBase.Pages.About
{
	internal class AboutViewModel : PageViewModelBase
	{
		public string VersionText => "GoBase for ArcGIS Pro 0.1";
		public string CopyrightText => "© 2026 GoBase. All rights reserved.";
		public string ServerText => $"Connected to {ApiClient.Instance.ServerUrl}";
		public ICommand OpenPortalCommand { get; }

		public AboutViewModel()
		{
			OpenPortalCommand = new RelayCommand(() => Open(ApiClient.Instance.ServerUrl));
		}

		private static void Open(string url)
		{
			if (!string.IsNullOrEmpty(url))
				Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
	}
}
