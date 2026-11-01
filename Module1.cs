using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace GoBase
{
	internal class Module1 : Module
	{
		private static Module1 _this = null;

		/// <summary>
		/// Retrieve the singleton instance to this module here
		/// </summary>
		public static Module1 Current => _this ??= (Module1)FrameworkApplication.FindModule("GoBase_Module");

		#region Overrides
		// Pro loads the module at start-up (autoLoad in Config.daml), so raster layers in a reopened
		// project get new credentials without the GoBase panel being opened.
		protected override bool Initialize()
		{
			Services.RasterCredentialRefresher.Start();
			return base.Initialize();
		}

		protected override void Uninitialize()
		{
			Services.RasterCredentialRefresher.Stop();
			base.Uninitialize();
		}

		/// <summary>
		/// Called by Framework when ArcGIS Pro is closing
		/// </summary>
		/// <returns>False to prevent Pro from closing, otherwise True</returns>
		protected override bool CanUnload()
		{
			return true;
		}

		#endregion Overrides
	}
}
