using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fluid;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OrchardCore.Autoroute.Drivers;
using OrchardCore.Autoroute.Models;
using OrchardCore.Autoroute.ViewModels;
using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Handlers;
using OrchardCore.ContentManagement.Metadata;
using OrchardCore.ContentManagement.Records;
using OrchardCore.ContentManagement.Routing;
using OrchardCore.Environment.Cache;
using OrchardCore.Liquid;
using OrchardCore.Settings;
using YesSql;

namespace OrchardCore.Autoroute.Handlers
{
    public class AutoroutePartHandler : ContentPartHandler<AutoroutePart>
    {
        private readonly IAutorouteEntries _entries;
        private readonly AutorouteOptions _options;
        private readonly ILiquidTemplateManager _liquidTemplateManager;
        private readonly IContentDefinitionManager _contentDefinitionManager;
        private readonly ISiteService _siteService;
        private readonly ITagCache _tagCache;
        private readonly ISession _session;
        private readonly IServiceProvider _serviceProvider;

        private IContentManager _contentManager;

        public AutoroutePartHandler(
            IAutorouteEntries entries,
            IOptions<AutorouteOptions> options,
            ILiquidTemplateManager liquidTemplateManager,
            IContentDefinitionManager contentDefinitionManager,
            ISiteService siteService,
            ITagCache tagCache,
            ISession session,
            IServiceProvider serviceProvider)
        {
            _entries = entries;
            _options = options.Value;
            _liquidTemplateManager = liquidTemplateManager;
            _contentDefinitionManager = contentDefinitionManager;
            _siteService = siteService;
            _tagCache = tagCache;
            _session = session;
            _serviceProvider = serviceProvider;
        }

        public override async Task PublishedAsync(PublishContentContext context, AutoroutePart part)
        {
            // Add parent content item path, and children, only if parent has a valid path.
            if (!String.IsNullOrWhiteSpace(part.Path))
            {
                var entriesToAdd = new List<AutorouteEntry>
                {
                    new AutorouteEntry(part.ContentItem.ContentItemId, part.Path)
                };

                if (part.RouteContainedItems)
                {
                    _contentManager ??= _serviceProvider.GetRequiredService<IContentManager>();

                    var containedAspect = await _contentManager.PopulateAspectAsync<ContainedContentItemsAspect>(context.PublishingItem);

                    await PopulateContainedContentItemRoutes(entriesToAdd, part.ContentItem.ContentItemId, containedAspect, context.PublishingItem.Content as JObject, part.Path);
                }

                _entries.AddEntries(entriesToAdd);
            }

            if (part.SetHomepage)
            {
                var site = await _siteService.LoadSiteSettingsAsync();

                if (site.HomeRoute == null)
                {
                    site.HomeRoute = new RouteValueDictionary();
                }

                var homeRoute = site.HomeRoute;

                foreach (var entry in _options.GlobalRouteValues)
                {
                    homeRoute[entry.Key] = entry.Value;
                }

                homeRoute[_options.ContentItemIdKey] = context.ContentItem.ContentItemId;

                // Once we too the flag into account we can dismiss it.
                part.SetHomepage = false;
                await _siteService.UpdateSiteSettingsAsync(site);
            }

            // Evict any dependent item from cache
            await RemoveTagAsync(part);
        }

        public override Task UnpublishedAsync(PublishContentContext context, AutoroutePart part)
        {
            if (!String.IsNullOrWhiteSpace(part.Path))
            {
                _entries.RemoveEntry(part.ContentItem.ContentItemId, part.Path);

                // Evict any dependent item from cache
                return RemoveTagAsync(part);
            }

            return Task.CompletedTask;
        }

        public override Task RemovedAsync(RemoveContentContext context, AutoroutePart part)
        {
            if (!String.IsNullOrWhiteSpace(part.Path))
            {
                _entries.RemoveEntry(part.ContentItem.ContentItemId, part.Path);

                // Evict any dependent item from cache
                return RemoveTagAsync(part);
            }

            return Task.CompletedTask;
        }

        public override async Task UpdatedAsync(UpdateContentContext context, AutoroutePart part)
        {
            await GenerateContainerPathFromPattern(part);
            await GenerateContainedPathsFromPattern(context.UpdatingItem, part);
        }

        public async override Task CloningAsync(CloneContentContext context, AutoroutePart part)
        {
            var clonedPart = context.CloneContentItem.As<AutoroutePart>();
            clonedPart.Path = await GenerateUniqueAbsolutePathAsync(part.Path, context.CloneContentItem.ContentItemId);
            clonedPart.SetHomepage = false;
            clonedPart.Apply();

            await GenerateContainedPathsFromPattern(context.CloneContentItem, part);
        }

        public override Task GetContentItemAspectAsync(ContentItemAspectContext context, AutoroutePart part)
        {
            return context.ForAsync<RouteHandlerAspect>(aspect =>
            {
                aspect.Path = part.Path;
                aspect.Absolute = part.Absolute;
                aspect.Disabled = part.Disabled;

                return Task.CompletedTask;
            });
        }

        private Task RemoveTagAsync(AutoroutePart part)
        {
            return _tagCache.RemoveTagAsync($"slug:{part.Path}");
        }

        private async Task GenerateContainedPathsFromPattern(ContentItem contentItem, AutoroutePart part)
        {
            // Validate contained content item routes if container has valid path.
            if (!String.IsNullOrWhiteSpace(part.Path) || !part.RouteContainedItems)
            {
                return;
            }

            _contentManager ??= _serviceProvider.GetRequiredService<IContentManager>();

            var containedAspect = await _contentManager.PopulateAspectAsync<ContainedContentItemsAspect>(contentItem);

            // Build the entries for this content item to evaluate for duplicates.
            var entries = new List<AutorouteEntry>();
            await PopulateContainedContentItemRoutes(entries, part.ContentItem.ContentItemId, containedAspect, contentItem.Content as JObject, part.Path);

            await ValidateContainedContentItemRoutes(entries, part.ContentItem.ContentItemId, containedAspect, contentItem.Content as JObject, part.Path);
        }

        private async Task PopulateContainedContentItemRoutes(List<AutorouteEntry> entries, string containerContentItemId, ContainedContentItemsAspect containedContentItemsAspect, JObject content, string basePath)
        {
            foreach (var accessor in containedContentItemsAspect.Accessors)
            {
                var jItems = accessor.Invoke(content);

                foreach (JObject jItem in jItems)
                {
                    var contentItem = jItem.ToObject<ContentItem>();
                    var handlerAspect = await _contentManager.PopulateAspectAsync<RouteHandlerAspect>(contentItem);

                    if (!handlerAspect.Disabled)
                    {
                        var path = handlerAspect.Path;
                        if (!handlerAspect.Absolute)
                        {
                            path = (basePath.EndsWith('/') ? basePath : basePath + '/') + handlerAspect.Path;
                        }

                        entries.Add(new AutorouteEntry(containerContentItemId, path, contentItem.ContentItemId, jItem.Path));
                    }

                    var itemBasePath = (basePath.EndsWith('/') ? basePath : basePath + '/') + handlerAspect.Path;
                    var childrenAspect = await _contentManager.PopulateAspectAsync<ContainedContentItemsAspect>(contentItem);
                    await PopulateContainedContentItemRoutes(entries, containerContentItemId, childrenAspect, jItem, itemBasePath);
                }
            }
        }

        private async Task ValidateContainedContentItemRoutes(List<AutorouteEntry> entries, string containerContentItemId, ContainedContentItemsAspect containedContentItemsAspect, JObject content, string basePath)
        {
            foreach (var accessor in containedContentItemsAspect.Accessors)
            {
                var jItems = accessor.Invoke(content);

                foreach (JObject jItem in jItems)
                {
                    var contentItem = jItem.ToObject<ContentItem>();
                    var containedAutoroutePart = contentItem.As<AutoroutePart>();

                    // This is only relevant if the content items have an autoroute part as we adjust the part value as required to guarantee a unique route.
                    // Content items routed only through the handler aspect already guarantee uniqueness.
                    if (containedAutoroutePart != null && !containedAutoroutePart.Disabled)
                    {
                        var path = containedAutoroutePart.Path;

                        if (containedAutoroutePart.Absolute && !await IsAbsolutePathUniqueAsync(path, contentItem.ContentItemId))
                        {
                            path = await GenerateUniqueAbsolutePathAsync(path, contentItem.ContentItemId);
                            containedAutoroutePart.Path = path;
                            containedAutoroutePart.Apply();

                            // Merge because we have disconnected the content item from it's json owner.
                            jItem.Merge(contentItem.Content, new JsonMergeSettings
                            {
                                MergeArrayHandling = MergeArrayHandling.Replace,
                                MergeNullValueHandling = MergeNullValueHandling.Merge
                            });
                        }
                        else
                        {
                            var currentItemBasePath = basePath.EndsWith('/') ? basePath : basePath + '/';
                            path = currentItemBasePath + containedAutoroutePart.Path;
                            if (!IsRelativePathUnique(entries, path, containedAutoroutePart))
                            {
                                path = GenerateRelativeUniquePath(entries, path, containedAutoroutePart);
                                // Remove base path and update part path.
                                containedAutoroutePart.Path = path.Substring(currentItemBasePath.Length);
                                containedAutoroutePart.Apply();

                                // Merge because we have disconnected the content item from it's json owner.
                                jItem.Merge(contentItem.Content, new JsonMergeSettings
                                {
                                    MergeArrayHandling = MergeArrayHandling.Replace,
                                    MergeNullValueHandling = MergeNullValueHandling.Merge
                                });
                            }

                            path = path.Substring(currentItemBasePath.Length);
                        }

                        var containedItemBasePath = (basePath.EndsWith('/') ? basePath : basePath + '/') + path;
                        var childItemAspect = await _contentManager.PopulateAspectAsync<ContainedContentItemsAspect>(contentItem);
                        await ValidateContainedContentItemRoutes(entries, containerContentItemId, childItemAspect, jItem, containedItemBasePath);
                    }
                }
            }
        }

        private bool IsRelativePathUnique(List<AutorouteEntry> entries, string path, AutoroutePart context)
        {
            var result = !entries.Any(e => context.ContentItem.ContentItemId != e.ContainedContentItemId && String.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private string GenerateRelativeUniquePath(List<AutorouteEntry> entries, string path, AutoroutePart context)
        {
            var version = 1;
            var unversionedPath = path;

            var versionSeparatorPosition = path.LastIndexOf('-');
            if (versionSeparatorPosition > -1 && int.TryParse(path.Substring(versionSeparatorPosition).TrimStart('-'), out version))
            {
                unversionedPath = path.Substring(0, versionSeparatorPosition);
            }

            while (true)
            {
                // Unversioned length + seperator char + version length.
                var quantityCharactersToTrim = unversionedPath.Length + 1 + version.ToString().Length - AutoroutePartDisplay.MaxPathLength;
                if (quantityCharactersToTrim > 0)
                {
                    unversionedPath = unversionedPath.Substring(0, unversionedPath.Length - quantityCharactersToTrim);
                }

                var versionedPath = $"{unversionedPath}-{version++}";
                if (IsRelativePathUnique(entries, versionedPath, context))
                {
                    //TODO maybe findlast index is better, because it would do the last item entered.
                    // consider.
                    var entryIndex = entries.FindIndex(e => e.ContainedContentItemId == context.ContentItem.ContentItemId);
                    var entry = entries[entryIndex];
                    entry.Path = versionedPath;
                    entries[entryIndex] = entry;

                    return versionedPath;
                }
            }
        }

        private async Task GenerateContainerPathFromPattern(AutoroutePart part)
        {
            // Compute the Path only if it's empty
            if (!String.IsNullOrWhiteSpace(part.Path))
            {
                return;
            }

            var pattern = GetPattern(part);

            if (!String.IsNullOrEmpty(pattern))
            {
                var model = new AutoroutePartViewModel()
                {
                    Path = part.Path,
                    AutoroutePart = part,
                    ContentItem = part.ContentItem
                };

                part.Path = await _liquidTemplateManager.RenderAsync(pattern, NullEncoder.Default, model,
                    scope => scope.SetValue("ContentItem", model.ContentItem));

                part.Path = part.Path.Replace("\r", String.Empty).Replace("\n", String.Empty);

                if (part.Path?.Length > AutoroutePartDisplay.MaxPathLength)
                {
                    part.Path = part.Path.Substring(0, AutoroutePartDisplay.MaxPathLength);
                }

                if (!await IsAbsolutePathUniqueAsync(part.Path, part.ContentItem.ContentItemId))
                {
                    part.Path = await GenerateUniqueAbsolutePathAsync(part.Path, part.ContentItem.ContentItemId);
                }

                part.Apply();
            }
        }

        /// <summary>
        /// Get the pattern from the AutoroutePartSettings property for its type
        /// </summary>
        private string GetPattern(AutoroutePart part)
        {
            var contentTypeDefinition = _contentDefinitionManager.GetTypeDefinition(part.ContentItem.ContentType);
            var contentTypePartDefinition = contentTypeDefinition.Parts.FirstOrDefault(x => String.Equals(x.PartDefinition.Name, "AutoroutePart"));
            var pattern = contentTypePartDefinition.GetSettings<AutoroutePartSettings>().Pattern;

            return pattern;
        }

        private async Task<string> GenerateUniqueAbsolutePathAsync(string path, string contentItemId)
        {
            var version = 1;
            var unversionedPath = path;

            var versionSeparatorPosition = path.LastIndexOf('-');
            if (versionSeparatorPosition > -1 && int.TryParse(path.Substring(versionSeparatorPosition).TrimStart('-'), out version))
            {
                unversionedPath = path.Substring(0, versionSeparatorPosition);
            }

            while (true)
            {
                // Unversioned length + seperator char + version length.
                var quantityCharactersToTrim = unversionedPath.Length + 1 + version.ToString().Length - AutoroutePartDisplay.MaxPathLength;
                if (quantityCharactersToTrim > 0)
                {
                    unversionedPath = unversionedPath.Substring(0, unversionedPath.Length - quantityCharactersToTrim);
                }

                var versionedPath = $"{unversionedPath}-{version++}";
                if (await IsAbsolutePathUniqueAsync(versionedPath, contentItemId))
                {
                    return versionedPath;
                }
            }
        }

        private async Task<bool> IsAbsolutePathUniqueAsync(string path, string contentItemId)
        {
            return (await _session.QueryIndex<AutoroutePartIndex>(o => o.ContentItemId != contentItemId && o.Path == path).CountAsync()) == 0;
        }
    }
}
