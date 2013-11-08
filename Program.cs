using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Services.Protocols;
using Escc.Cms.ResourceHelper.CmsResourcesProxy;
using EsccWebTeam.Cms;
using log4net;
using Microsoft.ApplicationBlocks.ExceptionManagement;
using Microsoft.ContentManagement.Publishing;

namespace Escc.Cms.ResourceHelper
{
    /// <summary>
    /// Compares CMS Resources found on editing and public environments, and either report the difference or delete resources found only in the public environment
    /// </summary>
    /// <remarks>
    /// Usage: 
    /// Escc.Cms.ResourceHelper [path to gallery] [/delete] [> report.csv]
    /// </remarks>
    class Program
    {
        // REMEMBER: if copying logging code, set assembly attribute for Log4Net in AssemblyInfo.cs
        private static ILog log = LogManager.GetLogger(typeof(Program));

        /// <summary>
        /// Parse the arguments for the programme and start either the reporting or deleting process
        /// </summary>
        /// <param name="args">The args.</param>
        static void Main(string[] args)
        {
            try
            {
                // Is the delete action specified as a parameter?
                bool delete = false;
                var len = args.Length;
                for (var i = 0; i < len; i++)
                {
                    if (args[i].ToUpperInvariant() == "/DELETE" || args[i].ToUpperInvariant() == "-DELETE")
                    {
                        delete = true;
                        args[i] = String.Empty;
                    }
                }

                // Is a specific gallery specified as a parameter?
                ResourceGallery startGallery = null;
                using (var cmsContext = new CmsApplicationContext())
                {
                    cmsContext.AuthenticateAsCurrentUser(delete ? PublishingMode.Update : PublishingMode.Published);
                    for (var i = 0; i < len; i++)
                    {
                        if (!String.IsNullOrEmpty(args[i]))
                        {
                            startGallery = CmsUtilities.ParseResourceGalleryUrl(args[i], cmsContext);

                            // If gallery was specified but not found, tell the user and finish
                            if (startGallery == null)
                            {
                                Console.WriteLine(String.Format(CultureInfo.CurrentCulture, Properties.Resources.ErrorGalleryNotFound, args[i]));
                                return;
                            }
                        }
                    }

                    // If method hasn't returned and there's no gallery yet, none was specified, so default to root
                    if (startGallery == null) startGallery = cmsContext.RootResourceGallery;


                    // Based on arguments, either delete resources from the gallery or just report what would be deleted
                    if (delete)
                    {
                        DeleteResources(cmsContext, startGallery);
                    }
                    else
                    {
                        ReportResources(startGallery);
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.Publish(ex);
                log.Error(ex.Message);
            }
        }

        /// <summary>
        /// Compare resources and output a report to the console in CSV format. Call app with "> report.csv" on command line to save report.
        /// </summary>
        /// <param name="startGallery">The start gallery.</param>
        private static void ReportResources(ResourceGallery startGallery)
        {
            // Compare resources for that gallery on edit and public environments
            var results = CompareResources(startGallery);
            var onlyOnPublic = results[0] as Dictionary<string, HierarchyItem>;
            var onlyOnEdit = results[1] as Dictionary<string, CmsResource>;

            // Output a report
            foreach (HierarchyItem item in onlyOnPublic.Values)
            {
                var resource = item as Resource;
                if (resource != null)
                {
                    Console.WriteLine(String.Format(CultureInfo.CurrentCulture, Properties.Resources.CsvDeleteResource, resource.Path, resource.DisplayName));
                    continue;
                }

                var gallery = item as ResourceGallery;
                if (gallery != null)
                {
                    Console.WriteLine(String.Format(CultureInfo.CurrentCulture, Properties.Resources.CsvDeleteGallery, gallery.Path, String.Empty));
                }
            }
            foreach (CmsResource resource in onlyOnEdit.Values)
            {
                Console.WriteLine(String.Format(CultureInfo.CurrentCulture, Properties.Resources.CsvUploadResource, resource.Path, resource.DisplayName));
            }
        }

        /// <summary>
        /// Compares the resources in the same resource gallery on the public and editing servers.
        /// </summary>
        /// <param name="publicGallery">The public gallery.</param>
        /// <returns></returns>
        private static object[] CompareResources(ResourceGallery publicGallery)
        {
            // Create lists to group the resources into
            var onlyOnPublic = new Dictionary<string, HierarchyItem>();
            var onlyOnEdit = new Dictionary<string, CmsResource>();

            CompareGallery(publicGallery, onlyOnPublic, onlyOnEdit);

            // Return findings
            return new object[] { onlyOnPublic, onlyOnEdit };
        }

        /// <summary>
        /// Compares a single resource gallery and its child galleries.
        /// </summary>
        /// <param name="publicGallery">The public gallery.</param>
        /// <param name="onlyOnPublic">Container for the resources only in the public gallery.</param>
        /// <param name="onlyOnEdit">Container for the resources only in the editing gallery.</param>
        private static void CompareGallery(ResourceGallery publicGallery, Dictionary<string, HierarchyItem> onlyOnPublic, Dictionary<string, CmsResource> onlyOnEdit)
        {
            // Get resources from editing environment
            CmsResourceGallery editGallery = null;
            try
            {
                using (var proxy = new CmsResourcesProxy.ResourcesProxy())
                {
                    proxy.UseDefaultCredentials = true;
                    editGallery = proxy.ResourceGalleryProxy(new Guid(publicGallery.Guid));
                }
            }
            catch (SoapException ex)
            {
                ex.Data.Add("Gallery path requested", publicGallery.Path);
                ex.Data.Add("Gallery GUID requested", publicGallery.Guid);
                ExceptionManager.Publish(ex);
                throw;
            }

            // If the entire gallery isn't on the edit server, make a note of that
            if (editGallery == null)
            {
                onlyOnPublic.Add(publicGallery.Guid.ToUpperInvariant(), publicGallery);
            }

            // Index the files on the edit server by their guid
            var filesOnEdit = new Dictionary<string, CmsResource>();
            if (editGallery != null)
            {
                foreach (CmsResource editResource in editGallery.Resources)
                {
                    filesOnEdit.Add(editResource.Guid.ToString("B").ToUpperInvariant(), editResource);
                }
            }

            // Go through the files on the public server, and see whether they're on the editing server too
            var matched = new Dictionary<string, CmsResource>();
            foreach (Resource publicResource in publicGallery.Resources)
            {
                if (filesOnEdit.ContainsKey(publicResource.Guid))
                {
                    matched.Add(publicResource.Guid.ToUpperInvariant(), filesOnEdit[publicResource.Guid]);
                }
                else
                {
                    onlyOnPublic.Add(publicResource.Guid.ToUpperInvariant(), publicResource);
                }
            }

            // Now go through the files on the editing server, and make sure they're on the public server too
            if (editGallery != null)
            {
                foreach (CmsResource editResource in editGallery.Resources)
                {
                    if (!matched.ContainsKey(editResource.Guid.ToString("B").ToUpperInvariant()))
                    {
                        onlyOnEdit.Add(editResource.Guid.ToString("B").ToUpperInvariant(), editResource);
                    }
                }
            }

            // And do the same for any child galleries
            foreach (ResourceGallery child in publicGallery.ResourceGalleries) CompareGallery(child, onlyOnPublic, onlyOnEdit);
        }

        /// <summary>
        /// Compare resources on two servers, delete those only on the public server and report by email those only on the editing server.
        /// </summary>
        /// <param name="cmsContext">The CMS context.</param>
        /// <param name="startGallery">The start gallery.</param>
        private static void DeleteResources(CmsContext cmsContext, ResourceGallery startGallery)
        {
            // Compare resources for that gallery on edit and public environments
            var results = CompareResources(startGallery);
            var onlyOnPublic = results[0] as Dictionary<string, HierarchyItem>;

            // Delete those which shouldn't be there
            try
            {
                foreach (HierarchyItem item in onlyOnPublic.Values)
                {
                    if (item.CanDelete && !item.IsDeleted)
                    {
                        item.Delete();
                        log.Info(String.Format(CultureInfo.CurrentCulture, Properties.Resources.LogDeleting, item.Path));
                    }
                }

                cmsContext.CommitAll();
            }
            catch (Exception)
            {
                cmsContext.RollbackAll();
                throw;
            }
        }
    }
}
