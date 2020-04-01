﻿using System;
using System.Web;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;

using CMS.DataEngine;
using CMS.DocumentEngine;
using CMS.FormEngine;
using CMS.EventLog;
using CMS.Relationships;
using CMS.SiteProvider;
using CMS.Base.Web.UI;
using CMS.Localization;
using CMS.Taxonomy;

namespace Kentico.EMS.Kontent.Publishing
{
    internal class PageSync : SyncBase
    {
        private const int ITEM_NAME_MAXLENGTH = 50;

        private AssetSync _assetSync;

        public PageSync(SyncSettings settings, AssetSync assetSync) : base(settings)
        {
            _assetSync = assetSync;
        }

        public bool IsAtSynchronizedSite(TreeNode node)
        {
            var siteId = SiteInfoProvider.GetSiteID(Settings.Sitename);

            return node.NodeSiteID == siteId;
        }

        public bool CanBePublished(TreeNode node)
        {
            return IsAtSynchronizedSite(node) && DocumentHelper.GetPublished(new NodeWithoutPublishFrom(node));
        }

        public static string GetPageExternalId(Guid nodeGuid)
        {
            return $"node|{nodeGuid}";
        }

        private string GetVariantEndpoint(TreeNode node)
        {
            var itemExternalId = GetPageExternalId(node.NodeGUID);
            var cultureInfo = CultureInfoProvider.GetCultureInfo(node.DocumentCulture);
            if (cultureInfo == null)
            {
                throw new InvalidOperationException($"Document culture '{node.DocumentCulture}' not found.");
            }

            var endpoint = $"/items/external-id/{HttpUtility.UrlEncode(itemExternalId)}/variants/codename/{HttpUtility.UrlEncode(cultureInfo.CultureCode)}";

            // Use this when endpoints by external ID are supported
            // var languageExternalId = LanguageSync.GetLanguageExternalId(cultureInfo.CultureGUID);
            // var endpoint = $"/items/external-id/{HttpUtility.UrlEncode(itemExternalId)}/variants/external-id/{HttpUtility.UrlEncode(languageExternalId)}";

            return endpoint;
        }

        private async Task UnpublishVariant(TreeNode node)
        {
            var variantEndpoint = GetVariantEndpoint(node);
            var endpoint = $"{variantEndpoint}/unpublish";

            await ExecuteWithoutResponse(endpoint, HttpMethod.Put);
        }

        public async Task UnpublishPage(TreeNode node)
        {
            try
            {
                SyncLog.LogEvent(EventType.INFORMATION, "KenticoKontentPublishing", "UNPUBLISHPAGE", $"{node.NodeAliasPath} - {node.DocumentCulture} ({node.NodeGUID})");

                if (node == null)
                {
                    throw new ArgumentNullException(nameof(node));
                }

                await CancelScheduling(node);
                await UnpublishVariant(node);
            }
            catch (HttpException ex)
            {
                switch (ex.GetHttpCode())
                {
                    case 404:
                    case 400:
                        // May not be there yet, 404 and 400 is OK
                        break;

                    default:
                        throw;
                }
            }
            catch (Exception ex)
            {
                SyncLog.LogException("KenticoKontentPublishing", "UNPUBLISHPAGE", ex, 0, $"{node.NodeAliasPath} - {node.DocumentCulture} ({node.NodeGUID})");
                throw;
            }
        }

        private async Task<bool> DeleteVariant(TreeNode node)
        {
            var endpoint = GetVariantEndpoint(node);

            try
            {
                await ExecuteWithoutResponse(endpoint, HttpMethod.Delete);

                return true;
            }
            catch (HttpException ex)
            {
                if (ex.GetHttpCode() == 404)
                {
                    // May not be there yet, 404 is OK but we want to report it
                    return false;
                }

                throw;
            }
        }

        public async Task DeletePage(CancellationToken? cancellation, TreeNode node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            try
            {
                SyncLog.LogEvent(EventType.INFORMATION, "KenticoKontentPublishing", "DELETEPAGE", $"{node.NodeAliasPath} - {node.DocumentCulture} ({node.NodeGUID})");

                var variantDeleted = await DeleteVariant(node);
            }
            catch (Exception ex)
            {
                SyncLog.LogException("KenticoKontentPublishing", "DELETEPAGE", ex, 0, $"{node.NodeAliasPath} - {node.DocumentCulture} ({node.NodeGUID})");
                throw;
            }
        }

        public async Task SyncAllPages(CancellationToken? cancellation, DataClassInfo contentType = null, string path = null)
        {
            if (contentType == null)
            {
                throw new ArgumentNullException(nameof(contentType));
            }

            try
            {
                SyncLog.Log($"Synchronizing pages for content type {contentType.ClassDisplayName}");

                SyncLog.LogEvent(EventType.INFORMATION, "KenticoKontentPublishing", "SYNCALLPAGES", contentType.ClassDisplayName);

                var documents = new DocumentQuery(contentType.ClassName)
                    .OnSite(Settings.Sitename)
                    .AllCultures()
                    .PublishedVersion();

                var documentsOnPath = String.IsNullOrEmpty(path) ?
                    documents :
                    documents.Path(path, PathTypeEnum.Section);

                var index = 0;

                foreach (var node in documents)
                {
                    if (cancellation?.IsCancellationRequested == true)
                    {
                        return;
                    }

                    index++;

                    SyncLog.Log($"Synchronizing page { node.NodeAliasPath} - { node.DocumentCulture} ({ node.NodeGUID}) - {index}/{documents.Count}");

                    await SyncPage(cancellation, node);
                }
            }
            catch (Exception ex)
            {
                SyncLog.LogException("KenticoKontentPublishing", "SYNCALLPAGES", ex);
                throw;
            }
        }

        private async Task UpsertItem(TreeNode node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            var externalId = GetPageExternalId(node.NodeGUID);
            var endpoint = $"/items/external-id/{HttpUtility.UrlEncode(externalId)}";

            var pageType = DataClassInfoProvider.GetDataClassInfo(node.NodeClassName);
            if (pageType == null)
            {
                throw new InvalidOperationException($"Page type {node.NodeClassName} not found.");
            }

            var formInfo = FormHelper.GetFormInfo(node.ClassName, false);
            if (formInfo == null)
            {
                throw new InvalidOperationException($"Form info for {node.NodeClassName} not found.");
            }

            var name = node.NodeName.LimitedTo(ITEM_NAME_MAXLENGTH);
            if (string.IsNullOrEmpty(name))
            {
                name = node.NodeAliasPath.LimitedTo(ITEM_NAME_MAXLENGTH);
            }

            var payload = new
            {
                name,
                type = new
                {
                    external_id = ContentTypeSync.GetPageTypeExternalId(pageType.ClassGUID)
                },
                sitemap_locations = new object[0]
            };

            await ExecuteWithoutResponse(endpoint, HttpMethod.Put, payload);
        }

        private async Task UpsertVariant(TreeNode node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            var externalId = GetPageExternalId(node.NodeGUID);
            var endpoint = GetVariantEndpoint(node);

            var contentType = DataClassInfoProvider.GetDataClassInfo(node.NodeClassName);
            if (contentType == null)
            {
                throw new InvalidOperationException($"Content type {node.NodeClassName} not found.");
            }

            var formInfo = FormHelper.GetFormInfo(node.ClassName, false);
            if (formInfo == null)
            {
                throw new InvalidOperationException($"Form info for {node.NodeClassName} not found.");
            }

            var fieldsToSync = ContentTypeSync.GetItemsToSync(node.NodeClassName).OfType<FormFieldInfo>();
            var fieldElements = await Task.WhenAll(
                fieldsToSync.Select(async (field) => new
                {
                    element = new
                    {
                        external_id = ContentTypeSync.GetFieldExternalId(contentType.ClassGUID, field.Guid)
                    },
                    value = await GetElementValue(node, field)
                })
            );
            var unsortedAttachmentsElement = new
            {
                element = new
                {
                    external_id = ContentTypeSync.GetFieldExternalId(contentType.ClassGUID, ContentTypeSync.UNSORTED_ATTACHMENTS_GUID)
                },
                value = (object)GetAttachmentGuids(node, null).Select(guid => new {
                    external_id = AssetSync.GetAttachmentExternalId(guid)
                })
            };
            var categoriesElement = new
            {
                element = new
                {
                    external_id = ContentTypeSync.GetFieldExternalId(contentType.ClassGUID, TaxonomySync.CATEGORIES_GUID)
                },
                value = (object)GetCategoryGuids(node).Select(guid => new {
                    external_id = TaxonomySync.GetCategoryTermExternalId(guid)
                }).ToList()
            };

            var siteId = SiteInfoProvider.GetSiteID(Settings.Sitename);

            var relationshipsQuery = new DataQuery()
                .From(
@"
CMS_Relationship R
LEFT JOIN CMS_Tree T ON R.RightNodeID = NodeID
FULL OUTER JOIN CMS_RelationshipNameSite RNS ON R.RelationshipNameID = RNS.RelationshipNameID
LEFT JOIN CMS_RelationshipName RN ON RNS.RelationshipNameID = RNS.RelationshipNameID
"
                )
                .Columns("NodeGUID", "RelationshipGUID")
                .WhereEqualsOrNull("LeftNodeID", node.NodeID)
                .WhereEqualsOrNull("RelationshipNameIsAdHoc", false)
                .WhereEquals("RNS.SiteID", siteId)
                .OrderBy("RelationshipOrder");

            var relationshipElements = relationshipsQuery
                .Result
                .Tables[0]
                .AsEnumerable()
                .GroupBy(row => (Guid)row["RelationshipGUID"])
                .Select(group => new {
                    element = new
                    {
                        external_id = ContentTypeSync.GetFieldExternalId(ContentTypeSync.RELATED_PAGES_GUID, group.Key)
                    },
                    value = (object)group
                        .Where(row => row["NodeGUID"] != DBNull.Value)
                        .Select(row => new { external_id = GetPageExternalId((Guid)row["NodeGUID"]) })
                        .ToList()
                })
                .ToList();

            var payload = new
            {
                elements = fieldElements
                    .Concat(new[] {
                        unsortedAttachmentsElement,
                        categoriesElement,
                    })
                    .Concat(relationshipElements)
                    .ToList()
            };

            await ExecuteWithoutResponse(endpoint, HttpMethod.Put, payload, true);
        }
        
        private async Task PublishVariant(TreeNode node, DateTime? publishWhen)
        {
            var variantEndpoint = GetVariantEndpoint(node);
            var endpoint = $"{variantEndpoint}/publish";

            var isScheduledToFuture = publishWhen.HasValue && (publishWhen.Value > DateTime.Now.AddMinutes(1));
            var payload = isScheduledToFuture ?
                new
                {
                    scheduled_to = publishWhen.Value.ToUniversalTime()
                } :
                null;

            await ExecuteWithoutResponse(endpoint, HttpMethod.Put, payload);
        }

        private async Task CancelScheduling(TreeNode node)
        {
            try
            {
                var variantEndpoint = GetVariantEndpoint(node);
                var endpoint = $"{variantEndpoint}/cancel-scheduled-publish";

                await ExecuteWithoutResponse(endpoint, HttpMethod.Put);
            }
            catch (HttpException ex)
            {
                switch (ex.GetHttpCode())
                {
                    // The request may end up with 404 Not found if the variant wasn't imported yet
                    // Or it may end up with 400 Bad request if not scheduled
                    // Anyway, it is easier to fail and forget this way than finding out if current workflow step through GET endpoints for workflow and variant
                    case 400:
                    case 404:
                        break;

                    default:
                        throw;
                }
            }
        }

        private async Task CreateNewVersion(TreeNode node)
        {
            try
            {
                var externalId = GetPageExternalId(node.NodeGUID);
                var variantEndpoint = GetVariantEndpoint(node);
                var endpoint = $"{variantEndpoint}/new-version";

                await ExecuteWithoutResponse(endpoint, HttpMethod.Put);
            }
            catch (HttpException ex)
            {
                switch (ex.GetHttpCode())
                {
                    case 404:
                    case 400:
                        // The request may end up with 400 Bad request if the item is already there, but not published.
                        // The request may end up with 404 Not found if the variant wasn't imported yet

                        // It is easier to fail and forget this way than finding out if current workflow step through GET endpoints for workflow and variant
                        break;

                    default:
                        throw;
                }
            }
        }

        public async Task SyncAllCultures(CancellationToken? cancellation, int nodeId)
        {
            // Sync all language versions of the document
            var nodes = DocumentHelper.GetDocuments().WhereEquals("NodeID", nodeId).WithCoupledColumns().AllCultures();
            foreach (var node in nodes)
            {
                if (IsAtSynchronizedSite(node) && CanBePublished(node))
                {
                    await SyncPage(cancellation, node);
                }
            }
        }

        public async Task SyncPage(CancellationToken? cancellation, TreeNode node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            if (!CanBePublished(node))
            {
                // Not published pages should be deleted in KC, but we never delete their attachments, attachments always reflect state in the CMS_Attachment table
                await DeletePage(cancellation, node);
                return;
            }

            try
            {
                SyncLog.LogEvent(EventType.INFORMATION, "KenticoKontentPublishing", "SYNCPAGE", $"{node.NodeAliasPath} ({node.DocumentCulture}) {node.NodeGUID}");

                await UpsertItem(node);

                await CancelScheduling(node);
                await CreateNewVersion(node);
                await UpsertVariant(node);

                await PublishVariant(node, node.DocumentPublishFrom);
            }
            catch (Exception ex)
            {
                SyncLog.LogException("KenticoKontentPublishing", "SYNCPAGE", ex, 0, $"{node.NodeAliasPath} - {node.DocumentCulture} ({node.NodeGUID})");
                throw;
            }
        }

        private List<Guid> GetRelatedNodeGuids(TreeNode node, FormFieldInfo relationshipsField)
        {
            var relationshipName = RelationshipNameInfoProvider.GetAdHocRelationshipNameCodeName(node.ClassName, relationshipsField);
            var relationshipNameInfo = RelationshipNameInfoProvider.GetRelationshipNameInfo(relationshipName);
            if (relationshipNameInfo == null)
            {
                return new List<Guid>();
            }

            var guids = RelationshipInfoProvider.GetRelationships()
                .Columns("NodeGUID")
                .Source(
                    s => s.LeftJoin(new QuerySourceTable("CMS_Tree"), "RightNodeID", "NodeID")
                )
                .WhereEquals("LeftNodeID", node.NodeID)
                .WhereEquals("RelationshipID", relationshipNameInfo.RelationshipNameId)
                .OrderBy("RelationshipOrder")
                .Result
                .Tables[0]
                .AsEnumerable()
                .Select(row => (Guid)row["NodeGUID"])
                .ToList();

            return guids;
        }

        private List<Guid> GetAttachmentGuids(TreeNode node, FormFieldInfo attachmentsField)
        {
            var guidsQuery = AttachmentInfoProvider.GetAttachments(node.DocumentID, false)
                .ExceptVariants()
                .Columns("AttachmentGUID")
                .OrderBy("AttachmentOrder");

            var narrowedQuery = (attachmentsField != null)
                ? guidsQuery.WhereEquals("AttachmentGroupGUID", attachmentsField.Guid)
                : guidsQuery.WhereTrue("AttachmentIsUnsorted");

            var guids = guidsQuery
                .Result
                .Tables[0]
                .AsEnumerable()
                .Select(row => (Guid)row["AttachmentGUID"])
                .ToList();

            return guids;
        }

        private List<Guid> GetCategoryGuids(TreeNode node)
        {
            var siteId = SiteInfoProvider.GetSiteID(Settings.Sitename);

            var guids = new DataQuery()
                .From(
                    new QuerySource(
                        new QuerySourceTable("CMS_DocumentCategory", "DC")
                    ).LeftJoin(
                        new QuerySourceTable("CMS_Category", "C"),
                        "DC.CategoryID",
                        "C.CategoryID"
                    )
                )
                .Columns("CategoryGUID")
                .WhereEquals("DocumentID", node.DocumentID)
                .WhereEqualsOrNull("CategorySiteID", siteId)
                .WhereNull("CategoryUserID")
                .Result
                .Tables[0]
                .AsEnumerable()
                .Select(row => (Guid)row["CategoryGUID"])
                .ToList();

            return guids;
        }

        private string TranslateMediaQuery(string query)
        {
            if (!string.IsNullOrEmpty(query))
            {
                var newQueryParams = new List<KeyValuePair<string, string>>();

                var queryParams = HttpUtility.ParseQueryString(HttpUtility.HtmlDecode(query));
                foreach (var key in queryParams.AllKeys)
                {
                    var newQueryParam = TranslateQueryParam(key, queryParams[key]);
                    if (newQueryParam != null)
                    {
                        newQueryParams.AddRange(newQueryParam);
                    }
                }

                if (newQueryParams.Count > 0)
                {
                    var newQuery = $"?{ string.Join("&", newQueryParams.Select(param => $"{HttpUtility.UrlEncode(param.Key)}={HttpUtility.UrlEncode(param.Value)}"))}";

                    return newQuery;
                }
            }

            return null;
        }

        private static Regex UrlAttributeRegEx = new Regex("(?<start>\\b(href|src)=\")(?<url>[^\"?]+)(?<query>\\?[^\"]+)?(?<end>\")");

        private async Task<string> ReplaceMediaLink(Match match)
        {
            var start = Convert.ToString(match.Groups["start"]);
            var url = HttpUtility.HtmlDecode(Convert.ToString(match.Groups["url"]));
            var query = HttpUtility.HtmlDecode(Convert.ToString(match.Groups["query"]));
            var end = Convert.ToString(match.Groups["end"]);

            try
            {
                // We need to set current site before every call to GetMediaData to avoid null reference
                SiteContext.CurrentSiteName = Settings.Sitename;
                var data = CMSDialogHelper.GetMediaData(url, Settings.Sitename);
                if (data != null)
                {
                    switch (data.SourceType)
                    {
                        case MediaSourceEnum.Attachment:
                        case MediaSourceEnum.DocumentAttachments:
                            {
                                var assetUrl = await _assetSync.GetAssetUrl("attachment", data.AttachmentGuid, data.FileName);
                                var newQuery = TranslateMediaQuery(query);

                                return $"{start}{HttpUtility.HtmlEncode(assetUrl)}{HttpUtility.HtmlEncode(newQuery)}{end}";
                            }

                        case MediaSourceEnum.MediaLibraries:
                            {
                                var assetUrl = await _assetSync.GetAssetUrl("media", data.MediaFileGuid, data.FileName);
                                var newQuery = TranslateMediaQuery(query);

                                return $"{start}{HttpUtility.HtmlEncode(assetUrl)}{HttpUtility.HtmlEncode(newQuery)}{end}";
                            }
                    }
                }
            }
            catch (Exception ex)
            {
                SyncLog.LogException("KenticoKontentPublishing", "TRANSLATEURL", ex, 0, $"Failed to replace media URL '{url + query}', keeping the original URL.");
            }

            // Keep as it is if translation is not successful, only resolve to absolute URL if needed
            if (url.StartsWith("~"))
            {
                return $"{start}{HttpUtility.HtmlEncode(Settings.WebRoot)}{HttpUtility.HtmlEncode(url.Substring(1))}{HttpUtility.HtmlEncode(query)}{end}";
            }
            return match.ToString();
        }

        private KeyValuePair<string, string>[] TranslateQueryParam(string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "width":
                    return new[] { new KeyValuePair<string, string>("w", value) };

                case "height":
                    return new[] { new KeyValuePair<string, string>("h", value) };

                case "maxsidesize":
                    return new[] {
                        new KeyValuePair<string, string>("w", value),
                        new KeyValuePair<string, string>("h", value),
                        new KeyValuePair<string, string>("fit", "clip")
                    };

                default:
                    return null;
            }
        }

        private async Task<string> TranslateLinks(string content)
        {
            return await UrlAttributeRegEx.ReplaceAsync(content, ReplaceMediaLink);
        }

        private async Task<object> GetElementValue(TreeNode node, FormFieldInfo field)
        {
            var value = node.GetValue(field.Name);

            switch (field.DataType)
            {
                case FieldDataType.Binary:
                    if (value == null)
                    {
                        return null;
                    }
                    return (bool)value ? "true" : "false";

                case FieldDataType.Date:
                case FieldDataType.DateTime:
                    if (value == null)
                    {
                        return null;
                    }
                    return ((DateTime)value).ToUniversalTime();

                case FieldDataType.Decimal:
                case FieldDataType.Double:
                case FieldDataType.Integer:
                case FieldDataType.LongInteger:
                    return Convert.ToString(Convert.ToDecimal(value));

                case FieldDataType.File:
                    if (value == null)
                    {
                        return new object[0];
                    }
                    else
                    {
                        return new[] {
                            new { external_id = AssetSync.GetAttachmentExternalId((Guid)value) }
                        };
                    }

                case FieldDataType.DocAttachments:
                    return GetAttachmentGuids(node, field).Select(guid => new {
                        external_id = AssetSync.GetAttachmentExternalId(guid)
                    });

                case FieldDataType.DocRelationships:
                    return GetRelatedNodeGuids(node, field).Select(guid => new {
                        external_id = GetPageExternalId(guid)
                    });

                case FieldDataType.LongText:
                    return await TranslateLinks(Convert.ToString(value));

                case FieldDataType.Guid:
                case FieldDataType.Text:
                case FieldDataType.Xml:
                case FieldDataType.TimeSpan:
                default:
                    return Convert.ToString(value);
            }
        }

        public async Task SyncAllPages(CancellationToken? cancellation)
        {
            SyncLog.Log("Synchronizing pages");

            var contentTypes = DataClassInfoProvider.GetClasses()
                .WhereEquals("ClassIsDocumentType", true)
                .OnSite(Settings.Sitename);

            var index = 0;

            foreach (var contentType in contentTypes)
            {
                if (cancellation?.IsCancellationRequested == true) {
                    return;
                }

                index++;

                SyncLog.Log($"Synchronizing pages for content type {contentType.ClassDisplayName} ({index}/{contentTypes.Count})");

                await SyncAllPages(cancellation, contentType);
            }
        }

        public async Task DeleteAllItems(CancellationToken? cancellation, Guid? contentTypeId = null)
        {
            try
            {
                SyncLog.Log("Deleting all content items");

                SyncLog.LogEvent(EventType.INFORMATION, "KenticoKontentPublishing", "DELETEALLITEMS");

                var itemIds = await GetAllItemIds(contentTypeId);

                var index = 0;

                foreach (var itemId in itemIds)
                {
                    if (cancellation?.IsCancellationRequested == true)
                    {
                        return;
                    }

                    await DeleteItem(itemId);

                    index++;

                    if (index % 10 == 0)
                    {
                        SyncLog.Log($"Deleted content items ({index}/{itemIds.Count})");
                    }
                }
            }
            catch (Exception ex)
            {
                SyncLog.LogException("KenticoKontentPublishing", "DELETEALLPAGES", ex);
                throw;
            }
        }

        private async Task<Guid> GetContentTypeId(DataClassInfo contentType)
        {
            var externalId = ContentTypeSync.GetPageTypeExternalId(contentType.ClassGUID);
            var endpoint = $"/types/external-id/{HttpUtility.UrlEncode(externalId)}";

            var response = await ExecuteWithResponse<IdReference>(endpoint, HttpMethod.Get);
            if (response == null)
            {
                return Guid.Empty;
            }

            return response.Id;
        }

        private async Task DeleteItem(Guid itemId)
        {
            var endpoint = $"/items/{itemId}";

            await ExecuteWithoutResponse(endpoint, HttpMethod.Delete);
        }

        private async Task<List<Guid>> GetAllItemIds(Guid? contentTypeId, string continuationToken = null)
        {
            var endpoint = $"/items";

            var response = await ExecuteWithResponse<ItemsResponse>(endpoint, HttpMethod.Get, continuationToken);
            if (response == null)
            {
                return new List<Guid>();
            }

            var items = contentTypeId.HasValue ?
                response.Items.Where(item => item.Type.Id == contentTypeId.Value) :
                response.Items;

            var ids = items.Select(item => item.Id).ToList();

            if (
                (ids.Count > 0) &&
                (response.Pagination != null) &&
                !string.IsNullOrEmpty(response.Pagination.ContinuationToken) &&
                (response.Pagination.ContinuationToken != continuationToken)
            )
            {
                var nextIds = await GetAllItemIds(contentTypeId, response.Pagination.ContinuationToken);
                ids = ids.Concat(nextIds).ToList();
            }

            return ids;
        }
    }
}
