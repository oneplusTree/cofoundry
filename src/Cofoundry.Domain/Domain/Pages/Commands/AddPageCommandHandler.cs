﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cofoundry.Domain.Data;
using Cofoundry.Domain.CQS;
using Microsoft.EntityFrameworkCore;
using Cofoundry.Core.Validation;
using Cofoundry.Core.MessageAggregator;
using Cofoundry.Core;
using Cofoundry.Core.EntityFramework;

namespace Cofoundry.Domain
{
    public class AddPageCommandHandler 
        : IAsyncCommandHandler<AddPageCommand>
        , IPermissionRestrictedCommandHandler<AddPageCommand>
    {
        #region constructor

        private readonly CofoundryDbContext _dbContext;
        private readonly IQueryExecutor _queryExecutor;
        private readonly EntityAuditHelper _entityAuditHelper;
        private readonly EntityTagHelper _entityTagHelper;
        private readonly IPageCache _pageCache;
        private readonly IMessageAggregator _messageAggregator;
        private readonly IPageStoredProcedures _pageStoredProcedures;
        private readonly ITransactionScopeFactory _transactionScopeFactory;
        
        public AddPageCommandHandler(
            CofoundryDbContext dbContext,
            IQueryExecutor queryExecutor,
            EntityAuditHelper entityAuditHelper,
            EntityTagHelper entityTagHelper,
            IPageCache pageCache,
            IMessageAggregator messageAggregator,
            IPageStoredProcedures pageStoredProcedures,
            ITransactionScopeFactory transactionScopeFactory
            )
        {
            _dbContext = dbContext;
            _queryExecutor = queryExecutor;
            _entityAuditHelper = entityAuditHelper;
            _entityTagHelper = entityTagHelper;
            _pageCache = pageCache;
            _messageAggregator = messageAggregator;
            _pageStoredProcedures = pageStoredProcedures;
            _transactionScopeFactory = transactionScopeFactory;
        }

        #endregion

        #region Execute

        public async Task ExecuteAsync(AddPageCommand command, IExecutionContext executionContext)
        {
            // Custom Validation
            await ValidateIsPageUniqueAsync(command, executionContext);

            var page = await MapPage(command, executionContext);
            _dbContext.Pages.Add(page);

            using (var scope = _transactionScopeFactory.Create(_dbContext))
            {
                await _dbContext.SaveChangesAsync();
                await _pageStoredProcedures.UpdatePublishStatusQueryLookupAsync(page.PageId);

                scope.Complete();
            }

            _pageCache.ClearRoutes();

            // Set Ouput
            command.OutputPageId = page.PageId;

            await _messageAggregator.PublishAsync(new PageAddedMessage()
            {
                PageId = page.PageId,
                HasPublishedVersionChanged = command.Publish
            });
        }

        #endregion

        #region helpers

        private Locale GetLocale(int? localeId)
        {
            if (!localeId.HasValue) return null;

            var locale = _dbContext
                .Locales
                .SingleOrDefault(l => l.LocaleId == localeId);

            if (locale == null)
            {
                throw new PropertyValidationException("The selected locale does not exist.", "LocaleId");
            }
            if (!locale.IsActive)
            {
                throw new PropertyValidationException("The selected locale is not active and cannot be used.", "LocaleId");
            }

            return locale;
        }

        private async Task<PageDirectory> GetPageDirectoryAsync(int pageDirectoryId)
        {
            var pageDirectories = await _dbContext
                .PageDirectories
                .ToDictionaryAsync(w => w.PageDirectoryId);

            var pageDirectory = pageDirectories.GetOrDefault(pageDirectoryId);

            if (pageDirectory == null)
            {
                throw new PropertyValidationException("The selected page directory does not exist.", nameof(pageDirectory.PageDirectoryId));
            }

            CheckPageDirectoryIsActive(pageDirectory, pageDirectories);

            return pageDirectory;
        }

        private void CheckPageDirectoryIsActive(PageDirectory pageDirectory, Dictionary<int, PageDirectory> allPageDirectories)
        {
            if (!pageDirectory.IsActive)
            {
                throw new PropertyValidationException("The selected page directory is not active and cannot be used.", nameof(pageDirectory.PageDirectoryId));
            }

            if (pageDirectory.ParentPageDirectoryId.HasValue)
            {
                var parentDirectory = allPageDirectories.GetOrDefault(pageDirectory.ParentPageDirectoryId.Value);
                EntityNotFoundException.ThrowIfNull(parentDirectory, pageDirectory.ParentPageDirectoryId);
                CheckPageDirectoryIsActive(pageDirectory.ParentPageDirectory, allPageDirectories);
            }
        }

        private async Task<Page> MapPage(AddPageCommand command, IExecutionContext executionContext)
        {
            // Create Page
            var page = new Page();
            page.PageTypeId = (int)command.PageType;
            page.Locale = GetLocale(command.LocaleId);
            page.PageDirectory = await  GetPageDirectoryAsync(command.PageDirectoryId);

            _entityAuditHelper.SetCreated(page, executionContext);
            _entityTagHelper.UpdateTags(page.PageTags, command.Tags, executionContext);

            var pageTemplate = await GetTemplateAsync(command);

            if (command.PageType == PageType.CustomEntityDetails)
            {
                var definition = await GetCustomEntityDefinitionAsync(pageTemplate.CustomEntityDefinitionCode, executionContext);
                var rule = await GetAndValidateRoutingRuleAsync(command, definition, executionContext);

                page.CustomEntityDefinitionCode = pageTemplate.CustomEntityDefinitionCode;
                page.UrlPath = rule.RouteFormat;
            }
            else
            {
                page.UrlPath = command.UrlPath;
            }
            
            var pageVersion = new PageVersion();
            pageVersion.Title = command.Title;
            pageVersion.ExcludeFromSitemap = !command.ShowInSiteMap;
            pageVersion.MetaDescription = command.MetaDescription ?? string.Empty;
            pageVersion.OpenGraphTitle = command.OpenGraphTitle;
            pageVersion.OpenGraphDescription = command.OpenGraphDescription;
            pageVersion.OpenGraphImageId = command.OpenGraphImageId;
            pageVersion.PageTemplate = pageTemplate;

            if (command.Publish)
            {
                page.PublishStatusCode = PublishStatusCode.Published;
                page.PublishDate = command.PublishDate ?? executionContext.ExecutionDate;
                pageVersion.WorkFlowStatusId = (int)WorkFlowStatus.Published;
            }
            else
            {
                page.PublishStatusCode = PublishStatusCode.Unpublished;
                page.PublishDate = command.PublishDate;
                pageVersion.WorkFlowStatusId = (int)WorkFlowStatus.Draft;
            }
            _entityAuditHelper.SetCreated(pageVersion, executionContext);
            page.PageVersions.Add(pageVersion);

            return page;
        }

        private async Task<ICustomEntityRoutingRule> GetAndValidateRoutingRuleAsync(AddPageCommand command, CustomEntityDefinitionSummary definition, IExecutionContext ex)
        {
            var rules = await _queryExecutor.ExecuteAsync(new GetAllCustomEntityRoutingRulesQuery(), ex);
            var rule = rules.SingleOrDefault(r => r.RouteFormat == command.CustomEntityRoutingRule);

            if (rule == null)
            {
                throw new PropertyValidationException("Routing rule not found", "CustomEntityRoutingRule", command.CustomEntityRoutingRule);
            }

            if (rule.RequiresUniqueUrlSlug && !definition.ForceUrlSlugUniqueness)
            {
                throw new PropertyValidationException("Ths routing rule requires a unique url slug, but the selected custom entity does not enforce url slug uniqueness", "CustomEntityRoutingRule");
            }

            return rule;
        }

        private async Task<CustomEntityDefinitionSummary> GetCustomEntityDefinitionAsync(string customEntityDefinitionCode, IExecutionContext ex)
        {
            var definition = await _queryExecutor.ExecuteAsync(new GetCustomEntityDefinitionSummaryByCodeQuery(customEntityDefinitionCode), ex);
            if (definition == null)
            {
                throw new PropertyValidationException("Custom entity defintion does not exists", nameof(definition.CustomEntityDefinitionCode), customEntityDefinitionCode);
            }
            return definition;
        }

        private async Task<PageTemplate> GetTemplateAsync(AddPageCommand command)
        {
            var template = await _dbContext
                .PageTemplates
                .SingleOrDefaultAsync(t => t.PageTemplateId == command.PageTemplateId);

            if (template == null)
            {
                throw new PropertyValidationException("Template not found", nameof(command.PageTemplateId));
            }

            if (command.PageType == PageType.CustomEntityDetails)
            {
                if (!template.IsCustomEntityTemplate())
                {
                    throw new PropertyValidationException("Template does not support custom entities", nameof(command.PageTemplateId));
                }
            }

            return template;
        }

        #region uniqueness

        private async Task ValidateIsPageUniqueAsync(AddPageCommand command, IExecutionContext ex)
        {
            var query = CreateUniquenessQuery(command);
            var isUnique = await _queryExecutor.ExecuteAsync(query, ex);
            ValidateIsUnique(command, isUnique);
        }

        private IsPagePathUniqueQuery CreateUniquenessQuery(AddPageCommand command)
        {
            var query = new IsPagePathUniqueQuery();
            query.LocaleId = command.LocaleId;
            query.PageDirectoryId = command.PageDirectoryId;

            if (command.PageType == PageType.CustomEntityDetails)
            {
                query.UrlPath = command.CustomEntityRoutingRule;
            }
            else
            {
                query.UrlPath = command.UrlPath;
            }

            return query;
        }

        private void ValidateIsUnique(AddPageCommand command, bool isUnique)
        {
            if (!isUnique)
            {
                var message = $"A page already exists with the path '{command.UrlPath}' in that directory";
                throw new UniqueConstraintViolationException(message, "UrlPath", command.UrlPath);
            }
        }

        #endregion

        #endregion

        #region Permission

        public IEnumerable<IPermissionApplication> GetPermissions(AddPageCommand command)
        {
            yield return new PageCreatePermission();

            if (command.Publish)
            {
                yield return new PagePublishPermission();
            }
        }

        #endregion
    }
}
