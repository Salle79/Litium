﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Litium.Accelerator.Extensions;
using Litium.Accelerator.Routing;
using Litium.Accelerator.Search;
using Litium.Accelerator.Utilities;
using Litium.Accelerator.ViewModels.Brand;
using Litium.Accelerator.ViewModels.Search;
using Litium.FieldFramework;
using Litium.FieldFramework.FieldTypes;
using Litium.Foundation.Modules.ExtensionMethods;
using Litium.Foundation.Search;
using Litium.Framework.Search;
using Litium.Products;
using Litium.Runtime.AutoMapper;
using Litium.Runtime.DependencyInjection;
using Litium.Search;
using Litium.Web;
using Litium.Web.Customers.TargetGroups;
using Litium.Web.Customers.TargetGroups.Events;
using Litium.Web.Models.Globalization;
using Litium.Web.Models.Products;
using Nest;

namespace Litium.Accelerator.Searching
{
    [ServiceDecorator(typeof(ProductSearchService))]
    internal class ProductSearchServiceDecorator : ProductSearchService
    {
        private readonly ProductSearchService _parent;
        private readonly SearchClientService _searchClientService;
        private readonly RequestModelAccessor _requestModelAccessor;
        private readonly SearchResultTransformationService _searchResultTransformationService;
        private readonly SearchQueryBuilder _searchQueryBuilder;

        public ProductSearchServiceDecorator(
            ProductSearchService parent,
            SearchClientService searchClientService,
            RequestModelAccessor requestModelAccessor,
            SearchResultTransformationService searchResultTransformationService,
            SearchQueryBuilder searchQueryBuilder)
        {
            _parent = parent;
            _searchClientService = searchClientService;
            _requestModelAccessor = requestModelAccessor;
            _searchResultTransformationService = searchResultTransformationService;
            _searchQueryBuilder = searchQueryBuilder;
        }

        public override SearchResponse Search(SearchQuery searchQuery, IDictionary<string, ISet<string>> tags = null, bool addPriceFilterTags = false, bool addNewsFilterTags = false, bool addCategoryFilterTags = false)
        {
            if (!_searchClientService.IsConfigured)
            {
                return _parent.Search(
                    searchQuery,
                    tags: tags,
                    addPriceFilterTags: addPriceFilterTags,
                    addNewsFilterTags: addNewsFilterTags,
                    addCategoryFilterTags: addCategoryFilterTags);
            }

            if (string.IsNullOrEmpty(searchQuery.Text) && searchQuery.CategorySystemId == Guid.Empty && searchQuery.ProductListSystemId == null)
            {
                return null;
            }

            return new ElasticSearchResponse<ProductDocument>(_searchClientService
                .Search<ProductDocument>(CultureInfo.CurrentUICulture, selector => selector
                     .Skip((searchQuery.PageNumber - 1) * searchQuery.PageSize)
                     .Size(searchQuery.PageSize)
                     .QueryWithPermission(queryContainerDescriptor => _searchQueryBuilder.BuildQuery(
                         queryContainerDescriptor,
                         searchQuery,
                         tags,
                         addPriceFilterTags,
                         addNewsFilterTags,
                         addCategoryFilterTags,
                         true))
                     .Sort(sortDescriptor => _searchQueryBuilder.BuildSorting(
                         sortDescriptor,
                         searchQuery))
                )
            );
        }

        public override SearchResult Transform(SearchQuery searchQuery, SearchResponse searchResponse)
        {
            if (!(searchResponse is ElasticSearchResponse<ProductDocument> elasticSearchResponse))
            {
                return _parent.Transform(searchQuery, searchResponse);
            }

            return _searchResultTransformationService.Transform(
                searchQuery,
                elasticSearchResponse.Response,
                _requestModelAccessor.RequestModel.ChannelModel.SystemId);
        }

        public override List<TagTerms> GetTagTerms(SearchQuery searchQuery, IEnumerable<string> tagNames)
        {
            if(!_searchClientService.IsConfigured)
            {
                return _parent.GetTagTerms(searchQuery, tagNames);
            }

            var searchResponse = _searchClientService.Search<ProductDocument>(CultureInfo.CurrentCulture, descriptor => descriptor
                                                     .Size(0)
                                                     .QueryWithPermission(queryContainerDescriptor => _searchQueryBuilder.BuildQuery(queryContainerDescriptor,
                                                                                                                                     searchQuery,
                                                                                                                                     tags: null,
                                                                                                                                     addPriceFilterTags: false,
                                                                                                                                     addNewsFilterTags: false,
                                                                                                                                     addCategoryFilterTags: false,
                                                                                                                                     addDefaultQuery: true))
                                                     .Aggregations(rootAgg =>
                                                     {
                                                         var aggs = new List<AggregationContainerDescriptor<ProductDocument>>();
                                                         aggs.AddRange(tagNames.Select(tagName => BuildFilterAggregation(rootAgg, tagName)));
                                                         aggs.Add(BuildTagAggregation(rootAgg, tagNames));

                                                         return aggs.Aggregate((a, b) => a & b);
                                                     }));

            var result = new List<TagTerms>();
            foreach (var tagName in tagNames)
            {
                var tag = CollectTagTerms(tagName);
                if (tag != null)
                {
                    result.Add(tag);
                }
            }

            return result;

            AggregationContainerDescriptor<ProductDocument> BuildFilterAggregation(AggregationContainerDescriptor<ProductDocument> container, string tagName)
            {
                return container
                       .Nested(tagName, nestedPerTag => nestedPerTag
                           .Path(x => x.Tags)
                           .Aggregations(tagAggregation => tagAggregation
                               .Filter("filter", tagFilter => tagFilter
                                   .Filter(filter => filter
                                       .Term(filterTerm => filterTerm
                                           .Field(field => field.Tags[0].Key)
                                           .Value(tagName)
                                       )
                                   )
                                   .Aggregations(tags => tags
                                       .Terms("tags", termSelector => termSelector
                                           .Field(field => field.Tags[0].Key)
                                           .Aggregations(subAggregation => subAggregation
                                               .Terms("tag", tag => tag
                                                   .Field(x => x.Tags[0].Value)
                                               )
                                           )
                                       )
                                   )
                               )
                           )
                       );
            }

            AggregationContainerDescriptor<ProductDocument> BuildTagAggregation(AggregationContainerDescriptor<ProductDocument> selector, IEnumerable<string> tagNames)
            {
                return selector
                       .Nested("$all-tags", filterContainer => filterContainer
                           .Path(x => x.Tags)
                           .Aggregations(a => a
                               .Filter("filter", filterSelector => filterSelector
                                   .Filter(ff => ff
                                       .Bool(bq => bq
                                           .Must(m => m
                                               .Terms(t => t
                                                   .Field(x => x.Tags[0].Key)
                                                   .Terms(tagNames)
                                               )
                                           )
                                       )
                                   )
                                   .Aggregations(termAgg => termAgg
                                       .Terms("tags", termSelector => termSelector
                                           .Field(x => x.Tags[0].Key)
                                           .Aggregations(subAggregation => subAggregation
                                               .Terms("tag", valueSelector => valueSelector
                                                   .Field(x => x.Tags[0].Value)
                                               )
                                           )
                                       )
                                   )
                               )
                           )
                       );
            }

            TagTerms CollectTagTerms(string tagName)
            {
                var fieldDefinition = tagName.GetFieldDefinitionForProducts();
                if (fieldDefinition == null)
                {
                    return null;
                }

                var allBuckets = searchResponse.Aggregations
                                               .Global("$all-tags")
                                               .Filter("filter")?
                                               .Terms("tags")?
                                               .Buckets
                                               .FirstOrDefault(x => x.Key.Equals(fieldDefinition.Id, StringComparison.OrdinalIgnoreCase))?
                                               .Terms("tag")?
                                               .Buckets;

                if (allBuckets == null)
                {
                    return null;
                }

                var topNode = searchResponse.Aggregations.Filter(fieldDefinition.Id);
                var tagBuckets = (topNode?.Nested(fieldDefinition.Id) ?? topNode)?.Filter("filter")?
                                                                                  .Terms("tags")?
                                                                                  .Buckets
                                                                                  .FirstOrDefault()?
                                                                                  .Terms("tag")?
                                                                                  .Buckets;

                var tagValues = new Dictionary<string, int>();
                foreach (var item in allBuckets)
                {
                    var current = tagBuckets?.FirstOrDefault(x => x.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
                    tagValues.Add(item.Key, unchecked((int)(current?.DocCount ?? 0)));
                }

                return new TagTerms
                {
                    TagName = fieldDefinition.Localizations.CurrentCulture.Name ?? fieldDefinition.Id,
                    TermCounts = tagValues
                        .Select(x =>
                        {
                            string key;
                            switch (fieldDefinition.FieldType)
                            {
                                case SystemFieldTypeConstants.Decimal:
                                case SystemFieldTypeConstants.Int:
                                    {
                                        key = x.Key.TrimStart('0');
                                        break;
                                    }
                                case SystemFieldTypeConstants.Date:
                                case SystemFieldTypeConstants.DateTime:
                                    {
                                        if (long.TryParse(x.Key, NumberStyles.Any, CultureInfo.InvariantCulture, out long l))
                                        {
                                            key = new DateTime(l).ToShortDateString();
                                        }
                                        else
                                        {
                                            goto default;
                                        }
                                        break;
                                    }
                                default:
                                    {
                                        key = x.Key;
                                        break;
                                    }
                            }

                            return new TermCount
                            {
                                Term = key,
                                Count = x.Value
                            };
                        })
                        .ToList()
                };
            }
        }

        [Service(ServiceType = typeof(SearchResultTransformationService), Lifetime = DependencyLifetime.Singleton)]
        internal class SearchResultTransformationService
        {
            private readonly BaseProductService _baseProductService;
            private readonly VariantService _variantService;
            private readonly FieldDefinitionService _fieldDefinitionService;
            private readonly ProductModelBuilder _productModelBuilder;
            private readonly UrlService _urlService;
            private readonly CategoryService _categoryService;

            public SearchResultTransformationService(
                BaseProductService baseProductService,
                VariantService variantService,
                FieldDefinitionService fieldDefinitionService,
                ProductModelBuilder productModelBuilder,
                UrlService urlService,
                CategoryService categoryService)
            {
                _baseProductService = baseProductService;
                _variantService = variantService;
                _fieldDefinitionService = fieldDefinitionService;
                _productModelBuilder = productModelBuilder;
                _urlService = urlService;
                _categoryService = categoryService;
            }

            public SearchResult Transform(SearchQuery searchQuery, ISearchResponse<ProductDocument> searchResponse, Guid channelSystemId)
            {
                var totalHits = unchecked((int)searchResponse.Total);
                return new SearchResult
                {
                    Items = new Lazy<IEnumerable<SearchResultItem>>(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(searchQuery.Text))
                        {
                            IoC.Resolve<TargetGroupEngine>().Process(new SearchEvent
                            {
                                SearchText = searchQuery.Text,
                                TotalHits = totalHits,
                            });
                        }

                        var products = CreateProductModel(searchResponse.Hits, searchQuery, channelSystemId);

                        return products
                            .Select(x => new ProductSearchResult
                            {
                                Item = x,
                                Id = x.SelectedVariant.SystemId,
                                Name = x.GetValue<string>(SystemFieldDefinitionConstants.Name),
                                Url = x.UseVariantUrl
                                    ? _urlService.GetUrl(x.SelectedVariant, new ProductUrlArgs(channelSystemId))
                                    : _urlService.GetUrl(x.BaseProduct, new ProductUrlArgs(channelSystemId))
                            }).ToList();
                    }),
                    PageSize = searchQuery.PageSize.Value,
                    Total = totalHits
                };
            }

            private IEnumerable<ProductModel> CreateProductModel(IReadOnlyCollection<IHit<ProductDocument>> hits, SearchQuery searchQuery, Guid channelSystemId)
            {
                if (hits.Count == 0)
                {
                    yield break;
                }

                foreach (var hit in hits)
                {
                    ProductModel model = null;

                    var item = hit.Source;
                    var variants = _variantService.Get(item.VariantSystemIds)
                        .Where(x => !string.IsNullOrEmpty(_urlService.GetUrl(x, new ProductUrlArgs(channelSystemId))))
                        .OrderBy(x => x.SortIndex);

                    if (item.IsBaseProduct)
                    {
                        var baseProduct = _baseProductService.Get(item.BaseProductSystemId);
                        if (baseProduct == null)
                        {
                            continue;
                        }

                        model = CreateProductModel(searchQuery, baseProduct, variants.ToList(), channelSystemId);
                    }
                    else
                    {
                        if (item.VariantSystemIds.Count > 1)
                        {
                            model = CreateProductModel(searchQuery, null, variants.ToList(), channelSystemId);
                        }
                        else
                        {
                            model = _productModelBuilder.BuildFromVariant(variants.FirstOrDefault());
                        }
                    }

                    if (model != null)
                    {
                        yield return model;
                    }
                }
            }

            private ProductModel CreateProductModel(SearchQuery searchQuery, BaseProduct baseProduct, ICollection<Variant> variants, Guid channelSystemId)
            {
                IEnumerable<Variant> currentVariants = variants;
                if (searchQuery.CategorySystemId != null && searchQuery.CategorySystemId != Guid.Empty)
                {
                    var product = baseProduct ?? _baseProductService.Get(currentVariants.First().BaseProductSystemId);
                    var categoryLink = _categoryService.Get(searchQuery.CategorySystemId.Value)?.ProductLinks.FirstOrDefault(x => x.BaseProductSystemId == product.SystemId);
                    if (categoryLink != null)
                    {
                        currentVariants = currentVariants.Where(x => categoryLink.ActiveVariantSystemIds.Contains(x.SystemId));
                    }
                }

                currentVariants = currentVariants
                    .Where(x => x.ChannelLinks.Any(z => z.ChannelSystemId == channelSystemId))
                    .OrderBy(x => x.SortIndex);

                if (searchQuery.Tags.Count > 0)
                {
                    var order = new ConcurrentDictionary<Variant, int>();
                    Variant firstVariant = null;
                    foreach (var tag in searchQuery.Tags)
                    {
                        var fieldDefinition = _fieldDefinitionService.Get<ProductArea>(tag.Key);
                        // ReSharper disable once PossibleMultipleEnumeration
                        foreach (var variant in currentVariants)
                        {
                            if (firstVariant == null)
                            {
                                firstVariant = variant;
                            }

                            var value = GetTranslatedValue((variant.Fields[tag.Key, CultureInfo.CurrentCulture] ?? variant.Fields[tag.Key]) as string, CultureInfo.CurrentCulture, fieldDefinition);
                            if (tag.Value.Contains(value))
                            {
                                order.AddOrUpdate(variant, _ => 1, (_, c) => c + 1);
                            }
                        }
                    }

                    if (order.Count > 0)
                    {
                        currentVariants = order.OrderByDescending(x => x.Value).Select(x => x.Key);
                    }
                }

                return baseProduct == null
                    ? _productModelBuilder.BuildFromVariant(currentVariants.First())
                    : _productModelBuilder.BuildFromBaseProduct(baseProduct, currentVariants.First());
            }

            private string GetTranslatedValue(string value, CultureInfo cultureInfo, FieldDefinition fieldDefinition)
            {
                if (fieldDefinition == null)
                {
                    return value;
                }

                if (fieldDefinition.FieldType == SystemFieldTypeConstants.TextOption)
                {
                    var option = fieldDefinition.Option as TextOption;

                    var item = option?.Items.FirstOrDefault(x => x.Value == value);
                    if (item != null && item.Name.TryGetValue(cultureInfo.Name, out string translation) && !string.IsNullOrEmpty(translation))
                    {
                        return translation;
                    }
                }

                return value;
            }
        }

        [Service(ServiceType = typeof(SearchQueryBuilder), Lifetime = DependencyLifetime.Scoped)]
        internal class SearchQueryBuilder
        {
            private readonly RequestModelAccessor _requestModelAccessor;
            private readonly SearchPriceFilterService _priceFilterService;
            private readonly PersonStorage _personStorage;

            private readonly Lazy<MarketModel> _marketModel;
            private readonly Lazy<Guid> _countrySystemId;
            private readonly Lazy<Guid> _assortmentSystemId;
            private readonly Lazy<SearchPriceFilterService.Container> _priceContainer;

            public SearchQueryBuilder(
                RequestModelAccessor requestModelAccessor,
                SearchPriceFilterService priceFilterService,
                PersonStorage personStorage)
            {
                _requestModelAccessor = requestModelAccessor;
                _priceFilterService = priceFilterService;
                _personStorage = personStorage;

                _marketModel = new Lazy<MarketModel>(() => _requestModelAccessor.RequestModel.ChannelModel?.Channel?.MarketSystemId?.MapTo<MarketModel>());
                _countrySystemId = new Lazy<Guid>(() => _requestModelAccessor.RequestModel.CountryModel.SystemId);
                _assortmentSystemId = new Lazy<Guid>(() => _marketModel.Value?.Market.AssortmentSystemId ?? Guid.Empty);
                _priceContainer = new Lazy<SearchPriceFilterService.Container>(() => _priceFilterService.GetPrices());
            }

            public QueryContainer BuildQuery(
                QueryContainerDescriptor<ProductDocument> qc,
                SearchQuery searchQuery,
                IDictionary<string, ISet<string>> tags = null,
                bool addPriceFilterTags = false,
                bool addNewsFilterTags = false,
                bool addCategoryFilterTags = false,
                bool addDefaultQuery = true)
            {
                var allQueries = new List<QueryContainer>();

                if (addDefaultQuery)
                {
                    allQueries.Add(qc.PublishedOnChannel());
                    allQueries.Add(qc.Bool(b => b.Filter(bf => bf.Term(t => t.Field(x => x.Assortments).Value(_assortmentSystemId.Value)))));
                    if (_personStorage.CurrentSelectedOrganization != null)
                    {
                        allQueries.Add((qc.Bool(b => b.Filter(bf => bf.Term(t => t.Field(x => x.Organizations).Value(Guid.Empty))))
                                        || qc.Bool(b => b.Filter(bf => bf.Term(t => t.Field(x => x.Organizations).Value(_personStorage.CurrentSelectedOrganization.SystemId))))));
                    }
                    else
                    {
                        allQueries.Add(qc.Bool(b => b.Filter(bf => bf.Term(t => t.Field(x => x.Organizations).Value(Guid.Empty)))));
                    }

                    if (searchQuery.ProductListSystemId == null)
                    {
                        if (searchQuery.CategorySystemId != null)
                        {
                            if (searchQuery.CategoryShowRecursively)
                            {
                                allQueries.Add(qc.Bool(b => b.Filter(bf => bf.Term(t => t.Field(x => x.ParentCategories).Value(searchQuery.CategorySystemId.Value)))));
                            }
                            else
                            {
                                allQueries.Add(qc.Bool(b => b.Filter(bf => bf.Term(t => t.Field(x => x.Categories).Value(searchQuery.CategorySystemId.Value)))));
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(searchQuery.Text))
                        {
                            var fuzziness = searchQuery.Text.Length > 2 ? Fuzziness.EditDistance(2) : Fuzziness.Auto;
                            allQueries.Add((qc.Match(x => x.Field(z => z.Name).Query(searchQuery.Text).Fuzziness(fuzziness).Boost(10).SynonymAnalyzer())
                                            || qc.Match(x => x.Field(z => z.ArticleNumber).Query(searchQuery.Text.ToLower()).Boost(2).SynonymAnalyzer())
                                            || qc.Match(x => x.Field(z => z.Content).Query(searchQuery.Text).Fuzziness(fuzziness).SynonymAnalyzer())));
                        }
                    }
                    else
                    {
                        allQueries.Add(qc.Bool(b => b.Filter(bf => bf.Term(t => t.Field(x => x.ProductLists).Value(searchQuery.ProductListSystemId.Value)))));
                    }

                    var pageModel = _requestModelAccessor.RequestModel.CurrentPageModel;
                    if (pageModel.IsBrandPageType())
                    {
                        if (tags != null)
                        {
                            if (!tags.ContainsKey(BrandListViewModel.TagName))
                            {
                                tags.Add(BrandListViewModel.TagName, new SortedSet<string>(new[] { pageModel.Page.Localizations.CurrentUICulture.Name }));
                            }
                        }
                        else
                        {
                            tags = new Dictionary<string, ISet<string>> { { BrandListViewModel.TagName, new SortedSet<string>(new[] { pageModel.Page.Localizations.CurrentUICulture.Name }) } };
                        }
                    }
                }

                if (tags != null)
                {
                    foreach (var tag in tags.Where(x => x.Value.Count > 0))
                    {
                        var filterTags = tag.Value
                            .Select<string, Func<QueryContainerDescriptor<ProductDocument>, QueryContainer>>(tagValue =>
                               s => s
                                .Nested(n => n
                                    .Path(x => x.Tags)
                                    .Query(nq
                                        => nq.Term(t => t.Field(f => f.Tags[0].Key).Value(tag.Key))
                                        && nq.Term(t => t.Field(f => f.Tags[0].Value).Value(tagValue))
                                    )
                                ));
                        allQueries.Add(qc.Bool(b => b.Filter(bf => bf.Bool(bb => bb.Should(filterTags)))));
                    }
                }

                if (addCategoryFilterTags)
                {
                    if (searchQuery.Category.Count > 0)
                    {
                        allQueries.Add(qc.Bool(b => b.Filter(bf => bf.Bool(bb => bb.Should(searchQuery.Category
                               .Select<Guid, Func<QueryContainerDescriptor<ProductDocument>, QueryContainer>>(x =>
                                   s => s.Term(t => t.Field(f => f.Categories).Value(x))))))));
                    }
                }

                if (addPriceFilterTags)
                {
                    var priceFilters = _priceFilterService
                        .GetPriceFilterTags(searchQuery, _priceContainer.Value, _countrySystemId.Value)
                        .ToList();

                    if (priceFilters.Count > 0)
                    {
                        allQueries.Add(qc.Bool(b => b.Filter(bf => bf.Bool(bb => bb.Should(priceFilters)))));
                    }
                }

                if (addNewsFilterTags)
                {
                    if (searchQuery.NewsDate != null)
                    {
                        allQueries.Add(qc.Bool(b => b.Filter(bf => bf.DateRange(r => r
                           .Field(x => x.NewsDate)
                           .GreaterThan(searchQuery.NewsDate.Item1)
                           .LessThan(searchQuery.NewsDate.Item2)))));
                    }
                }

                if (allQueries.Count == 0)
                {
                    return qc;
                }

                return allQueries.Aggregate((a, b) => a & b);
            }

            public IPromise<IList<ISort>> BuildSorting(SortDescriptor<ProductDocument> sortDescriptor, SearchQuery searchQuery)
            {
                var s = sortDescriptor;
                if (searchQuery.ProductListSystemId == null)
                {
                    switch (searchQuery.SortBy)
                    {
                        case SearchQueryConstants.Price:
                            var priceFilters = _priceFilterService
                                    .GetPriceFilterTags(searchQuery, _priceContainer.Value, _countrySystemId.Value)
                                    .ToList();

                            s = s.Field(f => new FieldSort
                            {
                                Field = Infer.Field<ProductDocument>(ff => ff.Prices[0].Price),
                                Order = searchQuery.SortDirection == SortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending,
                                Mode = SortMode.Min,
                                Nested = new NestedSort
                                {
                                    Path = Infer.Field<ProductDocument>(ff => ff.Prices),
                                    Filter = new QueryContainerDescriptor<ProductDocument>().Bool(b =>
                                       b.Filter(bf => bf.Bool(bb => bb.Should(priceFilters))))
                                }
                            });
                            break;

                        case SearchQueryConstants.Name:
                            // dont need any special sortings from the searchindex
                            break;

                        case SearchQueryConstants.News:
                            s = s.Descending(x => x.NewsDate);
                            break;

                        case SearchQueryConstants.Popular:
                            var websiteSystemId = _requestModelAccessor.RequestModel.WebsiteModel.SystemId;
                            s = s.Field(f => new FieldSort
                            {
                                Field = Infer.Field<ProductDocument>(ff => ff.MostSold[0].Quantity),
                                Order = SortOrder.Descending,
                                Missing = decimal.MaxValue,
                                Nested = new NestedSort
                                {
                                    Path = Infer.Field<ProductDocument>(ff => ff.MostSold),
                                    Filter = new TermQuery
                                    {
                                        Field = Infer.Field<ProductDocument>(ff => ff.MostSold[0].SystemId),
                                        Value = websiteSystemId
                                    }
                                }
                            });
                            break;

                        case SearchQueryConstants.Recommended:
                            if (searchQuery.CategorySystemId != null)
                            {
                                var categorySystemId = searchQuery.CategorySystemId.GetValueOrDefault();
                                s = s.Field(f => new FieldSort
                                {
                                    Field = Infer.Field<ProductDocument>(ff => ff.CategorySortIndex[0].SortIndex),
                                    Order = searchQuery.SortDirection == SortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending,
                                    Nested = new NestedSort
                                    {
                                        Path = Infer.Field<ProductDocument>(ff => ff.CategorySortIndex),
                                        Filter = new TermQuery
                                        {
                                            Field = Infer.Field<ProductDocument>(ff => ff.CategorySortIndex[0].SystemId),
                                            Value = categorySystemId
                                        }
                                    }
                                });
                            }
                            break;
                        default:
                            {
                                if (!string.IsNullOrWhiteSpace(searchQuery.Text) || _requestModelAccessor.RequestModel.CurrentPageModel.IsSearchResultPageType())
                                {
                                    // always sort products by their score, if no free-text is entered the score will be the same for all the products
                                    s = s.Descending(SortSpecialField.Score);
                                }
                                else
                                {
                                    if (searchQuery.Type == SearchType.Products)
                                    {
                                        goto case SearchQueryConstants.Popular;
                                    }
                                    if (searchQuery.Type == SearchType.Category)
                                    {
                                        goto case SearchQueryConstants.Recommended;
                                    }
                                }
                                goto case SearchQueryConstants.Name;
                            }
                    }

                    // default sorting is to always sort products after their name, article number
                    s = s
                        .Field(f => searchQuery.SortDirection == SortDirection.Ascending
                        ? f.Field(x => x.Name.Suffix("keyword")).Ascending()
                        : f.Field(x => x.Name.Suffix("keyword")).Descending())
                       .Ascending(x => x.ArticleNumber);
                }
                else
                {
                    switch (searchQuery.SortBy)
                    {
                        case SearchQueryConstants.Price:
                            s = s.Ascending(x => x.ArticleNumber);
                            break;
                        case SearchQueryConstants.Name:
                            s = s
                                .Field(f => searchQuery.SortDirection == SortDirection.Ascending
                                ? f.Field(x => x.Name.Suffix("keyword")).Ascending()
                                : f.Field(x => x.Name.Suffix("keyword")).Descending())
                               .Ascending(x => x.ArticleNumber);
                            break;
                        default:
                            var productListSystemId = searchQuery.ProductListSystemId.GetValueOrDefault();
                            s = s.Field(f => new FieldSort
                            {
                                Field = Infer.Field<ProductDocument>(ff => ff.ProductListSortIndex[0].SortIndex),
                                Order = searchQuery.SortDirection == SortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending,
                                Nested = new NestedSort
                                {
                                    Path = Infer.Field<ProductDocument>(ff => ff.ProductListSortIndex),
                                    Filter = new TermQuery
                                    {
                                        Field = Infer.Field<ProductDocument>(ff => ff.ProductListSortIndex[0].SystemId),
                                        Value = productListSystemId
                                    }
                                }
                            });
                            break;
                    }
                }
                return s;
            }
        }
    }
}
