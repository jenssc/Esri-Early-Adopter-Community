using ArcGIS.Core.Data;
using ArcGIS.Core.Hosting;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace MobileGeodatabase
{
    internal class Program
    {
        // ArcGIS Pro installation path. This is used to load ArcGIS Pro assemblies and native dlls.
        private static string _arcgisProPath = "";

        //[STAThread] must be present on the Application entry point
        [STAThread]
        static void Main(string[] args) {
            try {
                // Get the Pro installation path from registry 
                _arcgisProPath = GetInstallDirAndVersionFromReg().path;
                if (string.IsNullOrEmpty(_arcgisProPath)) throw new InvalidOperationException("ArcGIS Pro is not installed.");
                // Resolve ArcGIS Pro assembly paths
                AppDomain currentDomain = AppDomain.CurrentDomain;
                currentDomain.AssemblyResolve += new ResolveEventHandler(ResolveProAssemblyPath);
                // Perform CoreHost tasks
                PerformCoreHostTask(args);
            }
            catch (Exception ex) {
                // Error (missing installation, no license, 64 bit mismatch, etc.)
                Console.Error.WriteLine(ex.Message);
                if (ex.InnerException != null) {
                    Console.Error.WriteLine("Inner Exception:");
                    Console.Error.WriteLine(ex.InnerException);
                }
                return;
            }
        }

        private static void MyTask() {
            using var geodatabase = new Geodatabase(new MobileGeodatabaseConnectionPath(new Uri(System.IO.Path.GetFullPath("s100ed12.geodatabase"))));

            var definitionsFeatureClass = geodatabase.GetDefinitions<FeatureClassDefinition>();

            var definitionsTable = geodatabase.GetDefinitions<TableDefinition>();
        }

        private static void PerformCoreHostTask(string[] args) {
            // Call Host.Initialize before constructing any objects from ArcGIS.Core
            try {
                Host.Initialize();

                MyTask();
            }
            catch (Exception e) {
                // Error (missing installation, no license, 64 bit mismatch, etc.)
                Console.WriteLine($@"Host.Initialize failed: {e.Message}");
                throw;
            }
            try {
                // Perform any tasks with ArcGIS Pro SDK for .NET here.
            }
            catch (Exception e) {
                // Error performing tasks with ArcGIS Pro SDK for .NET
                Console.WriteLine($@"Error performing tasks with ArcGIS Pro SDK for .NET: {e.Message}");
                throw;
            }
        }


        /// <summary>
        /// Resolves the ArcGIS Pro Assembly Path.  Called when loading of an assembly fails.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// <returns>programmatically loaded assembly in the pro /bin path</returns>
        static Assembly ResolveProAssemblyPath(object sender, ResolveEventArgs args) {
            string assemblyPath = Path.Combine(_arcgisProPath, "bin", new AssemblyName(args.Name).Name + ".dll");
            if (!File.Exists(assemblyPath)) return null;
            Assembly assembly = Assembly.LoadFrom(assemblyPath);
            return assembly;
        }

        /// <summary>
        /// Gets the ArcGIS Pro install location, major version, and build number from the registry.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception">InvalidOperationException</exception>
        internal static (string path, string version, string buildNo) GetInstallDirAndVersionFromReg() {
            string regKeyName = "ArcGISPro";
            string regPath = $@"SOFTWARE\ESRI\{regKeyName}";

            string err1 = $@"Install location of ArcGIS Pro cannot be found. Please check your registry for HKLM\{regPath}\InstallDir";
            string err2 = $@"Version of ArcGIS Pro cannot be determined. Please check your registry for HKLM\{regPath}\Version";
            string err3 = $@"Build Number of ArcGIS Pro cannot be determined. Please check your registry for HKLM\{regPath}\BuildNumber";
            string path;
            string version;
            string buildNo;
            try {
                RegistryKey localKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                RegistryKey esriKey = localKey.OpenSubKey(regPath);
                if (esriKey == null) {
                    localKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                    esriKey = localKey.OpenSubKey(regPath);
                }
                if (esriKey == null) {
                    //this is an error
                    throw new System.InvalidOperationException(err1);
                }
                path = esriKey.GetValue("InstallDir") as string;
                //this is an error
                if (path == null || path == string.Empty)
                    throw new InvalidOperationException(err1);
                version = esriKey.GetValue("Version") as string;
                //this is an error
                if (version == null || version == string.Empty)
                    throw new InvalidOperationException(err2);
                buildNo = esriKey.GetValue("BuildNumber") as string;
                //this is an error
                if (buildNo == null || buildNo == string.Empty)
                    throw new InvalidOperationException(err3);
            }
            catch (Exception ex) {
                throw new Exception(err1, ex);
            }
            return (path, version, buildNo);
        }
    }
}
