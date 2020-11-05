﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ServiceStack.Auth;
using ServiceStack.Configuration;
using ServiceStack.MiniProfiler;
using ServiceStack.Data;
using ServiceStack.OrmLite;
using ServiceStack.Text;
using ServiceStack.Web;

namespace ServiceStack
{
    public partial class AutoQueryFeature
    {
        public List<Action<AutoCrudMetadata>> AutoCrudMetadataFilters { get; set; } = new List<Action<AutoCrudMetadata>>
        {
            AuditAutoCrudMetadataFilter
        };
        
        public string AccessRole { get; set; } = RoleNames.Admin;

        public Dictionary<Type, string[]> ServiceRoutes { get; set; } = new Dictionary<Type, string[]> {
            { typeof(GetCrudEventsService), new []{ "/" + "crudevents".Localize() + "/{Model}" } },
            { typeof(CheckCrudEventService), new []{ "/" + "crudevents".Localize() + "/check" } },
        };

        public Action<CrudContext> OnBeforeCreate { get; set; }
        public Func<CrudContext,Task> OnBeforeCreateAsync { get; set; }
        public Action<CrudContext> OnAfterCreate { get; set; }
        public Func<CrudContext,Task> OnAfterCreateAsync { get; set; }

        public Action<CrudContext> OnBeforePatch { get; set; }
        public Func<CrudContext,Task> OnBeforePatchAsync { get; set; }
        public Action<CrudContext> OnAfterPatch { get; set; }
        public Func<CrudContext,Task> OnAfterPatchAsync { get; set; }

        public Action<CrudContext> OnBeforeUpdate { get; set; }
        public Func<CrudContext,Task> OnBeforeUpdateAsync { get; set; }
        public Action<CrudContext> OnAfterUpdate { get; set; }
        public Func<CrudContext,Task> OnAfterUpdateAsync { get; set; }

        public Action<CrudContext> OnBeforeDelete { get; set; }
        public Func<CrudContext,Task> OnBeforeDeleteAsync { get; set; }
        public Action<CrudContext> OnAfterDelete { get; set; }
        public Func<CrudContext,Task> OnAfterDeleteAsync { get; set; }

        protected void OnRegister(IAppHost appHost)
        {
            if (AccessRole != null && appHost.GetContainer().Exists<ICrudEvents>())
            {
                appHost.RegisterServices(ServiceRoutes);
            }
        }

        public static void AuditAutoCrudMetadataFilter(AutoCrudMetadata meta)
        {
            foreach (var applyAttr in meta.AutoApplyAttrs)
            {
                switch (applyAttr.Name)
                {
                    case Behavior.AuditQuery:
                        meta.Add(new AutoFilterAttribute(
                            QueryTerm.Ensure, nameof(AuditBase.DeletedDate), SqlTemplate.IsNull));
                        break;
                    case Behavior.AuditCreate:
                    case Behavior.AuditModify:
                        if (applyAttr.Name == Behavior.AuditCreate)
                        {
                            meta.Add(new AutoPopulateAttribute(nameof(AuditBase.CreatedDate)) {
                                Eval = "utcNow"
                            });
                            meta.Add(new AutoPopulateAttribute(nameof(AuditBase.CreatedBy)) {
                                Eval = "userAuthName"
                            });
                        }
                        meta.Add(new AutoPopulateAttribute(nameof(AuditBase.ModifiedDate)) {
                            Eval = "utcNow"
                        });
                        meta.Add(new AutoPopulateAttribute(nameof(AuditBase.ModifiedBy)) {
                            Eval = "userAuthName"
                        });
                        break;
                    case Behavior.AuditDelete:
                    case Behavior.AuditSoftDelete:
                        if (applyAttr.Name == Behavior.AuditSoftDelete)
                            meta.SoftDelete = true;

                        meta.Add(new AutoPopulateAttribute(nameof(AuditBase.DeletedDate)) {
                            Eval = "utcNow"
                        });
                        meta.Add(new AutoPopulateAttribute(nameof(AuditBase.DeletedBy)) {
                            Eval = "userAuthName"
                        });
                        break;
                }
            }
        }
    }
    
/* Allow metadata discovery & code-gen in *.Source.csproj builds */    
#if !SOURCE
    [Restrict(VisibilityTo = RequestAttributes.None)]
    public partial class GetCrudEventsService {}
    [Restrict(VisibilityTo = RequestAttributes.None)]
    public partial class CheckCrudEventService {}
#endif
    
    [DefaultRequest(typeof(GetCrudEvents))]
    public partial class GetCrudEventsService : Service
    {
        public IAutoQueryDb AutoQuery { get; set; }
        public IDbConnectionFactory DbFactory { get; set; }

        public async Task<object> Any(GetCrudEvents request)
        {
            var appHost = HostContext.AppHost;
            var feature = appHost.AssertPlugin<AutoQueryFeature>();
            await RequestUtils.AssertAccessRoleAsync(base.Request, accessRole:feature.AccessRole, authSecret:request.AuthSecret);

            if (string.IsNullOrEmpty(request.Model))
                throw new ArgumentNullException(nameof(request.Model));

            var dto = appHost.Metadata.FindDtoType(request.Model);
            var namedConnection = dto?.FirstAttribute<NamedConnectionAttribute>()?.Name;

            using var useDb = namedConnection != null
                ? await DbFactory.OpenDbConnectionAsync(namedConnection).ConfigAwait()
                : await DbFactory.OpenDbConnectionAsync().ConfigAwait();
            
            var q = AutoQuery.CreateQuery(request, Request, useDb);
            var response = await AutoQuery.ExecuteAsync(request, q, Request, useDb).ConfigAwait();
            
            return response;
        }
    }

    [DefaultRequest(typeof(CheckCrudEvents))]
    public partial class CheckCrudEventService : Service
    {
        public IDbConnectionFactory DbFactory { get; set; }

        public async Task<object> Any(CheckCrudEvents request)
        {
            var appHost = HostContext.AppHost;
            var feature = appHost.AssertPlugin<AutoQueryFeature>();
            await RequestUtils.AssertAccessRoleAsync(base.Request, accessRole:feature.AccessRole, authSecret:request.AuthSecret);

            if (string.IsNullOrEmpty(request.Model))
                throw new ArgumentNullException(nameof(request.Model));

            var ids = request.Ids?.Count > 0
                ? request.Ids
                : throw new ArgumentNullException(nameof(request.Ids));

            var dto = appHost.Metadata.FindDtoType(request.Model);
            var namedConnection = dto?.FirstAttribute<NamedConnectionAttribute>()?.Name;

            using var useDb = namedConnection != null
                ? await DbFactory.OpenDbConnectionAsync(namedConnection).ConfigAwait()
                : await DbFactory.OpenDbConnectionAsync().ConfigAwait();

            var q = useDb.From<CrudEvent>()
                .Where(x => x.Model == request.Model)
                .And(x => ids.Contains(x.ModelId))
                .SelectDistinct(x => x.ModelId);

            var results = await useDb.ColumnAsync<string>(q).ConfigAwait();
            return new CheckCrudEventsResponse {
                Results = results.ToList(),
            };
        }
    }

    public class CrudContext
    {
        public IRequest Request { get; private set; }
        public IAuthSession Session => Request.GetSession();
        public IDbConnection Db { get; private set; }
        public ICrudEvents Events { get; private set; }
        public string Operation { get; set; }
        public object Dto { get; private set; }
        public Type ModelType { get; private set; }
        public Type RequestType { get; private set; }
        public Type ResponseType { get; private set; }
        public ModelDefinition ModelDef { get; private set; }
        public PropertyAccessor IdProp { get; private set; }
        public PropertyAccessor ResultProp { get; private set; }
        public PropertyAccessor CountProp { get; private set; }
        public PropertyAccessor RowVersionProp { get; private set; }
        
        public object Id { get; set; }
        
        public object Response { get; set; }
        
        public long? RowsUpdated { get; set; }

        internal void SetResult(AutoQuery.ExecValue result)
        {
            Id = result.Id;
            RowsUpdated = result.RowsUpdated;
        }
        
        internal GetMemberDelegate RequestIdGetter() => 
            TypeProperties.Get(RequestType).GetPublicGetter(ModelDef.PrimaryKey.Name);
        
        internal void ThrowPrimaryKeyRequiredForRowVersion() =>
            throw new NotSupportedException($"Could not resolve Primary Key from '{RequestType.Name}' to be able to resolve RowVersion");

        internal static CrudContext Create<Table>(IRequest request, IDbConnection db, object dto, string operation)
        {
            var appHost = HostContext.AppHost;
            var requestType = dto?.GetType() ?? throw new ArgumentNullException(nameof(dto));
            var responseType = appHost.Metadata.GetOperation(requestType)?.ResponseType;
            var responseProps = responseType == null ? null : TypeProperties.Get(responseType);
            return new CrudContext {
                Operation = operation,
                Request = request ?? throw new ArgumentNullException(nameof(request)),
                Db = db ?? throw new ArgumentNullException(nameof(db)),
                Events = appHost.TryResolve<ICrudEvents>(),
                Dto = dto,
                ModelType = typeof(Table),
                RequestType = requestType,
                ModelDef = typeof(Table).GetModelMetadata(),
                ResponseType = responseType,
                IdProp = responseProps?.GetAccessor(Keywords.Id),
                CountProp = responseProps?.GetAccessor(Keywords.Count),
                ResultProp = responseProps?.GetAccessor(Keywords.Result),
                RowVersionProp = responseProps?.GetAccessor(Keywords.RowVersion),
            };
        }
    }

    public class AutoCrudMetadata
    {
        public Type DtoType { get; set; }
        public Type ModelType { get; set; }
        public ModelDefinition ModelDef { get; set; }
        public TypeProperties DtoProps { get; set; }
        public List<AutoPopulateAttribute> PopulateAttrs { get; set; } = new List<AutoPopulateAttribute>();
        public List<AutoFilterAttribute> AutoFilters { get; set; } = new List<AutoFilterAttribute>();
        public List<QueryDbFieldAttribute> AutoFiltersDbFields { get; set; } = new List<QueryDbFieldAttribute>();
        public List<AutoApplyAttribute> AutoApplyAttrs { get; set; } = new List<AutoApplyAttribute>();
        public Dictionary<string, AutoUpdateAttribute> UpdateAttrs { get; set; } = new Dictionary<string, AutoUpdateAttribute>();
        public Dictionary<string, AutoDefaultAttribute> DefaultAttrs { get; set; } = new Dictionary<string, AutoDefaultAttribute>();
        public Dictionary<string, AutoMapAttribute> MapAttrs { get; set; } = new Dictionary<string, AutoMapAttribute>();
        public HashSet<string> NullableProps { get; set; } = new HashSet<string>();
        public List<string> RemoveDtoProps { get; set; } = new List<string>();
        public GetMemberDelegate RowVersionGetter { get; set; }
        public bool SoftDelete { get; set; }
        
        static readonly ConcurrentDictionary<Type, AutoCrudMetadata> cache = 
            new ConcurrentDictionary<Type, AutoCrudMetadata>();

        internal static AutoCrudMetadata Create(Type dtoType)
        {
            if (cache.TryGetValue(dtoType, out var to))
                return to;

            to = new AutoCrudMetadata {
                DtoType = dtoType,
                ModelType = AutoCrudOperation.GetModelType(dtoType),
                DtoProps = TypeProperties.Get(dtoType),
            };
            if (to.ModelType != null)
                to.ModelDef = to.ModelType.GetModelMetadata();
            
            to.RowVersionGetter = to.DtoProps.GetPublicGetter(Keywords.RowVersion);
            
            var dtoAttrs = dtoType.AllAttributes();
            foreach (var dtoAttr in dtoAttrs)
            {
                if (dtoAttr is AutoPopulateAttribute populateAttr)
                {
                    to.Add(populateAttr);
                }
                else if (dtoAttr is AutoFilterAttribute filterAttr)
                {
                    to.Add(filterAttr);
                }
                else if (dtoAttr is AutoApplyAttribute applyAttr)
                {
                    to.AutoApplyAttrs.Add(applyAttr);
                }
            }

            foreach (var pi in to.DtoProps.PublicPropertyInfos)
            {
                var allAttrs = pi.AllAttributes();
                var propName = pi.Name;
            
                if (allAttrs.FirstOrDefault(x => x is AutoMapAttribute) is AutoMapAttribute mapAttr)
                {
                    to.Set(propName, mapAttr);
                    propName = mapAttr.To;
                }

                if (allAttrs.FirstOrDefault(x => x is AutoUpdateAttribute) is AutoUpdateAttribute updateAttr)
                {
                    to.Set(propName, updateAttr);
                }

                if (allAttrs.FirstOrDefault(x => x is AutoDefaultAttribute) is AutoDefaultAttribute defaultAttr)
                {
                    to.Set(propName, defaultAttr);
                }

                if (pi.PropertyType.IsNullableType())
                {
                    to.AddNullableProperty(propName);
                }

                if (!AutoQuery.IncludeCrudProperties.Contains(propName))
                {
                    var hasProp = to.ModelDef.GetFieldDefinition(propName) != null; 
                    if (!hasProp
                        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                        || (AutoQuery.IgnoreCrudProperties.Contains(pi.Name) && !hasProp)
                        || pi.HasAttribute<AutoIgnoreAttribute>())
                    {
                        to.AddDtoPropertyToRemove(pi);
                    }
                }
            }

            var feature = HostContext.AssertPlugin<AutoQueryFeature>();
            foreach (var fn in feature.AutoCrudMetadataFilters)
            {
                fn(to);
            }
            
            return cache[dtoType] = to;
        }

        public bool HasAutoApply(string name) => 
            AutoApplyAttrs != null && AutoApplyAttrs.Any(x => x.Name == name);

        public void AddDtoPropertyToRemove(PropertyInfo pi)
        {
            RemoveDtoProps.Add(pi.Name);
        }

        public void AddNullableProperty(string propName)
        {
            NullableProps.Add(propName);
        }

        public void Set(string propName, AutoMapAttribute mapAttr)
        {
            MapAttrs[propName] = mapAttr;
        }

        public void Set(string propName, AutoDefaultAttribute defaultAttr)
        {
            DefaultAttrs[propName] = defaultAttr;
        }

        public void Set(string propName, AutoUpdateAttribute updateAttr)
        {
            UpdateAttrs[propName] = updateAttr;
        }

        public void Add(AutoPopulateAttribute populateAttr)
        {
            PopulateAttrs.Add(populateAttr);
        }

        public void Add(AutoFilterAttribute filterAttr)
        {
            AutoFilters.Add(filterAttr);
            AutoFiltersDbFields.Add(ExprResult.ToDbFieldAttribute(filterAttr));
        }
    }

    public partial class AutoQuery : IAutoCrudDb
    {
        public static HashSet<string> IgnoreCrudProperties { get; } = new HashSet<string> {
            nameof(IHasSessionId.SessionId),
            nameof(IHasBearerToken.BearerToken),
            nameof(IHasVersion.Version),
        };
        
        public static HashSet<string> IncludeCrudProperties { get; set; } = new HashSet<string> {
            Keywords.Reset,
            Keywords.RowVersion,
        };

        public object Create<Table>(ICreateDb<Table> dto, IRequest req)
        {
            //TODO: Allow Create to use Default Values
            using var db = GetDb<Table>(req);
            using var profiler = Profiler.Current.Step("AutoQuery.Create");

            var ctx = CrudContext.Create<Table>(req,db,dto,AutoCrudOperation.Create);
            var feature = HostContext.GetPlugin<AutoQueryFeature>();
            feature.OnBeforeCreate?.Invoke(ctx);

            ctx.Response = ExecAndReturnResponse<Table>(ctx,
                ctx => {
                    var dtoValues = ResolveDtoValues(req, dto);
                    var pkField = ctx.ModelDef.PrimaryKey;
                    var selectIdentity = ctx.IdProp != null || ctx.ResultProp != null || ctx.Events != null;

                    //Use same Id if being executed from id
                    if (req.Items.TryGetValue(Keywords.EventModelId, out var eventId) && eventId != null 
                        && !dtoValues.ContainsKey(pkField.Name))
                    {
                        dtoValues[pkField.Name] = eventId.ConvertTo(pkField.PropertyInfo.PropertyType);
                        selectIdentity = false;
                    }

                    var isAutoId = pkField.AutoIncrement || pkField.AutoId;
                    if (!isAutoId)
                    {
                        selectIdentity = false;
                        var pkValue = dtoValues.TryGetValue(pkField.Name, out var value)
                            ? value
                            : null;
                        if (pkValue == null || pkValue.Equals(pkField.FieldTypeDefaultValue))
                            throw new ArgumentException(ErrorMessages.PrimaryKeyRequired, pkField.Name);
                    }

                    var autoIntId = db.Insert<Table>(dtoValues, selectIdentity: selectIdentity);
                    return CreateInternal(dtoValues, pkField, selectIdentity, autoIntId);
                });

            feature.OnAfterCreate?.Invoke(ctx);
            return ctx.Response;
        }

        public async Task<object> CreateAsync<Table>(ICreateDb<Table> dto, IRequest req)
        {
            //TODO: Allow Create to use Default Values
            using var db = GetDb<Table>(req);
            using var profiler = Profiler.Current.Step("AutoQuery.Create");

            var ctx = CrudContext.Create<Table>(req,db,dto,AutoCrudOperation.Create);
            var feature = HostContext.GetPlugin<AutoQueryFeature>();
            if (feature.OnBeforeCreateAsync != null)
                await feature.OnBeforeCreateAsync(ctx);
            
            ctx.Response = await ExecAndReturnResponseAsync<Table>(ctx,
                async ctx => {
                    var dtoValues = ResolveDtoValues(ctx.Request, ctx.Dto);
                    var pkField = ctx.ModelDef.PrimaryKey;
                    var selectIdentity = ctx.IdProp != null || ctx.ResultProp != null || ctx.Events != null;

                    //Use same Id if being executed from id
                    if (req.Items.TryGetValue(Keywords.EventModelId, out var eventId) && eventId != null 
                        && !dtoValues.ContainsKey(pkField.Name))
                    {
                        dtoValues[pkField.Name] = eventId.ConvertTo(pkField.PropertyInfo.PropertyType);
                        selectIdentity = false;
                    }

                    var isAutoId = pkField.AutoIncrement || pkField.AutoId;
                    if (!isAutoId)
                    {
                        selectIdentity = false;
                        var pkValue = dtoValues.TryGetValue(pkField.Name, out var value)
                            ? value
                            : null;
                        if (pkValue == null || pkValue.Equals(pkField.FieldTypeDefaultValue))
                            throw new ArgumentException(ErrorMessages.PrimaryKeyRequired, pkField.Name);
                    }
                    
                    var autoIntId = await db.InsertAsync<Table>(dtoValues, selectIdentity: selectIdentity).ConfigAwait();
                    return CreateInternal(dtoValues, pkField, selectIdentity, autoIntId);
                }).ConfigAwait();

            if (feature.OnAfterCreateAsync != null)
                await feature.OnAfterCreateAsync(ctx);
            return ctx.Response;
        }

        private static ExecValue CreateInternal(Dictionary<string, object> dtoValues,
            FieldDefinition pkField, bool selectIdentity, long autoIntId)
        {
            // [AutoId] Guid's populate the PK Property or return Id if provided
            var isAutoId = pkField?.AutoId == true;
            var providedId = pkField != null && dtoValues.ContainsKey(pkField.Name);
            if (isAutoId || providedId)
                return new ExecValue(pkField.GetValue(dtoValues), selectIdentity ? 1 : autoIntId);

            return selectIdentity
                ? new ExecValue(autoIntId, 1)
                : pkField != null && dtoValues.TryGetValue(pkField.Name, out var idValue)
                    ? new ExecValue(idValue, autoIntId)
                    : new ExecValue(null, autoIntId);
        }

        public object Update<Table>(IUpdateDb<Table> dto, IRequest req)
        {
            return UpdateInternal<Table>(req, dto,AutoCrudOperation.Update);
        }

        public Task<object> UpdateAsync<Table>(IUpdateDb<Table> dto, IRequest req)
        {
            return UpdateInternalAsync<Table>(req, dto, AutoCrudOperation.Update);
        }

        public object Patch<Table>(IPatchDb<Table> dto, IRequest req)
        {
            return UpdateInternal<Table>(req, dto, AutoCrudOperation.Patch);
        }

        public Task<object> PatchAsync<Table>(IPatchDb<Table> dto, IRequest req)
        {
            return UpdateInternalAsync<Table>(req, dto, AutoCrudOperation.Patch);
        }

        public object PartialUpdate<Table>(object dto, IRequest req) =>
            UpdateInternal<Table>(req, dto, AutoCrudOperation.Patch);

        public Task<object> PartialUpdateAsync<Table>(object dto, IRequest req) =>
            UpdateInternalAsync<Table>(req, dto, AutoCrudOperation.Patch);

        private object UpdateInternal<Table>(IRequest req, object dto, string operation)
        {
            var skipDefaults = operation == AutoCrudOperation.Patch;
            using var db = GetDb<Table>(req);
            using (Profiler.Current.Step("AutoQuery.Update"))
            {
                var ctx = CrudContext.Create<Table>(req,db,dto,operation);
                
                var feature = HostContext.GetPlugin<AutoQueryFeature>();
                if (skipDefaults)
                    feature.OnBeforePatch?.Invoke(ctx);
                else
                    feature.OnBeforeUpdate?.Invoke(ctx);

                ctx.Response = ExecAndReturnResponse<Table>(ctx,
                    ctx => {
                        var dtoValues = ResolveDtoValues(req, dto, skipDefaults);
                        var pkField = ctx.ModelDef?.PrimaryKey;
                        if (pkField == null)
                            throw new NotSupportedException($"Table '{typeof(Table).Name}' does not have a primary key");
                        if (!dtoValues.TryGetValue(pkField.Name, out var idValue) || AutoMappingUtils.IsDefaultValue(idValue))
                            throw new ArgumentNullException(pkField.Name);
                        
                        // Should only update a Single Row
                        var rowsUpdated = GetAutoFilterExpressions(ctx, dtoValues, out var expr, out var exprParams) 
                            ? ctx.Db.UpdateOnly<Table>(dtoValues, expr, exprParams.ToArray())
                            : ctx.Db.UpdateOnly<Table>(dtoValues);

                        if (rowsUpdated != 1)
                            throw new OptimisticConcurrencyException($"{rowsUpdated} rows were updated by '{dto.GetType().Name}'");

                        return new ExecValue(idValue, rowsUpdated);
                    }); //TODO: UpdateOnly

                if (skipDefaults)
                    feature.OnAfterPatch?.Invoke(ctx);
                else
                    feature.OnAfterUpdate?.Invoke(ctx);

                return ctx.Response;
            }
        }

        private async Task<object> UpdateInternalAsync<Table>(IRequest req, object dto, string operation)
        {
            var skipDefaults = operation == AutoCrudOperation.Patch;
            using var db = GetDb<Table>(req);
            using (Profiler.Current.Step("AutoQuery.Update"))
            {
                var ctx = CrudContext.Create<Table>(req,db,dto,operation);
                
                var feature = HostContext.GetPlugin<AutoQueryFeature>();
                if (skipDefaults)
                {
                    if (feature.OnBeforePatchAsync != null) 
                        await feature.OnBeforePatchAsync(ctx);
                }
                else
                {
                    if (feature.OnBeforeUpdateAsync != null) 
                        await feature.OnBeforeUpdateAsync(ctx);
                }
                
                ctx.Response = await ExecAndReturnResponseAsync<Table>(ctx, 
                    async ctx => {
                        var dtoValues = ResolveDtoValues(req, dto, skipDefaults);
                        var pkField = ctx.ModelDef?.PrimaryKey;
                        if (pkField == null)
                            throw new NotSupportedException($"Table '{typeof(Table).Name}' does not have a primary key");
                        if (!dtoValues.TryGetValue(pkField.Name, out var idValue) || AutoMappingUtils.IsDefaultValue(idValue))
                            throw new ArgumentNullException(pkField.Name);
                        
                        // Should only update a Single Row
                        var rowsUpdated = GetAutoFilterExpressions(ctx, dtoValues, out var expr, out var exprParams) 
                            ? await ctx.Db.UpdateOnlyAsync<Table>(dtoValues, expr, exprParams.ToArray()).ConfigAwait()
                            : await ctx.Db.UpdateOnlyAsync<Table>(dtoValues).ConfigAwait();

                        if (rowsUpdated != 1)
                            throw new OptimisticConcurrencyException($"{rowsUpdated} rows were updated by '{dto.GetType().Name}'");

                        return new ExecValue(idValue, rowsUpdated);
                    }).ConfigAwait(); //TODO: UpdateOnly

                if (skipDefaults)
                {
                    if (feature.OnAfterPatchAsync != null) 
                        await feature.OnAfterPatchAsync(ctx);
                }
                else
                {
                    if (feature.OnAfterUpdateAsync != null) 
                        await feature.OnAfterUpdateAsync(ctx);
                }

                return ctx.Response;
            }
        }

        public object Delete<Table>(IDeleteDb<Table> dto, IRequest req)
        {
            using var db = GetDb<Table>(req);
            using var profiler = Profiler.Current.Step("AutoQuery.Delete");

            var meta = AutoCrudMetadata.Create(dto.GetType());
            if (meta.SoftDelete)
                return PartialUpdate<Table>(dto, req);

            var ctx = CrudContext.Create<Table>(req,db,dto,AutoCrudOperation.Delete);
            var feature = HostContext.GetPlugin<AutoQueryFeature>();
            feature.OnBeforeDelete?.Invoke(ctx);

            ctx.Response = ExecAndReturnResponse<Table>(ctx,
                ctx => {
                    var dtoValues = ResolveDtoValues(ctx.Request, ctx.Dto, skipDefaults:true);
                    var idValue = ctx.ModelDef.PrimaryKey != null && dtoValues.TryGetValue(ctx.ModelDef.PrimaryKey.Name, out var oId)
                        ? oId
                        : null;
                    var q = DeleteInternal<Table>(ctx, dtoValues);
                    if (q != null)
                        return new ExecValue(idValue, ctx.Db.Delete(q));
                    return new ExecValue(idValue, ctx.Db.Delete<Table>(dtoValues));
                });
            
            feature.OnAfterDelete?.Invoke(ctx);
            return ctx.Response;
        }

        public async Task<object> DeleteAsync<Table>(IDeleteDb<Table> dto, IRequest req)
        {
            using var db = GetDb<Table>(req);
            using var profiler = Profiler.Current.Step("AutoQuery.Delete");

            var meta = AutoCrudMetadata.Create(dto.GetType());
            if (meta.SoftDelete)
                return await UpdateInternalAsync<Table>(req, dto, AutoCrudOperation.Patch).ConfigAwait();

            var ctx = CrudContext.Create<Table>(req,db,dto,AutoCrudOperation.Delete);
            var feature = HostContext.GetPlugin<AutoQueryFeature>();
            if (feature.OnBeforeDeleteAsync != null)
                await feature.OnBeforeDeleteAsync(ctx);
            
            ctx.Response = await ExecAndReturnResponseAsync<Table>(ctx,
                async ctx => {
                    var dtoValues = ResolveDtoValues(req, dto, skipDefaults:true);
                    var idValue = ctx.ModelDef.PrimaryKey != null && dtoValues.TryGetValue(ctx.ModelDef.PrimaryKey.Name, out var oId)
                        ? oId
                        : null;
                    var q = DeleteInternal<Table>(ctx, dtoValues);
                    if (q != null)
                        return new ExecValue(idValue, await ctx.Db.DeleteAsync(q).ConfigAwait());
                    return new ExecValue(idValue, await ctx.Db.DeleteAsync<Table>(dtoValues).ConfigAwait());
                }).ConfigAwait();
            
            if (feature.OnAfterDeleteAsync != null)
                await feature.OnAfterDeleteAsync(ctx);

            return ctx.Response;
        }

        internal SqlExpression<Table> DeleteInternal<Table>(CrudContext ctx, Dictionary<string, object> dtoValues)
        {
            //Should have at least 1 non-default filter
            if (dtoValues.Count == 0)
                throw new NotSupportedException($"'{ctx.RequestType.Name}' did not contain any filters");
                    
            // Should only update a Single Row
            if (GetAutoFilterExpressions(ctx, dtoValues, out var expr, out var exprParams))
            {
                //If there were Auto Filters, construct filter expression manually by adding any remaining DTO values
                foreach (var entry in dtoValues)
                {
                    var fieldDef = ctx.ModelDef.GetFieldDefinition(entry.Key);
                    if (fieldDef == null)
                        throw new NotSupportedException($"Unknown '{entry.Key}' Field in '{ctx.RequestType.Name}' IDeleteDb<{typeof(Table).Name}> Request");
                            
                    if (expr.Length > 0)
                        expr += " AND ";

                    var quotedColumn = ctx.Db.GetDialectProvider().GetQuotedColumnName(ctx.ModelDef, fieldDef);

                    expr += quotedColumn + " = {" + exprParams.Count + "}";
                    exprParams.Add(entry.Value);
                }

                var q = ctx.Db.From<Table>();
                q.Where(expr, exprParams.ToArray());
                return q;
            }
            return null;
        }

        public object Save<Table>(ISaveDb<Table> dto, IRequest req)
        {
            using var db = GetDb<Table>(req);
            using var profiler = Profiler.Current.Step("AutoQuery.Save");

            var row = dto.ConvertTo<Table>();
            var response = ExecAndReturnResponse<Table>(CrudContext.Create<Table>(req,db,dto,AutoCrudOperation.Save),
                ctx => {
                    ctx.Db.Save(row);
                    return SaveInternal(dto, ctx);
                }); 
                
            return response;
        }

        public async Task<object> SaveAsync<Table>(ISaveDb<Table> dto, IRequest req)
        {
            using var db = GetDb<Table>(req);
            using var profiler = Profiler.Current.Step("AutoQuery.Save");

            var row = dto.ConvertTo<Table>();
            var response = await ExecAndReturnResponseAsync<Table>(CrudContext.Create<Table>(req,db,dto,AutoCrudOperation.Save),
                async ctx => {
                    await ctx.Db.SaveAsync(row).ConfigAwait();
                    return SaveInternal(dto, ctx);
                }).ConfigAwait();
                
            return response;
        }

        private static ExecValue SaveInternal<Table>(ISaveDb<Table> dto, CrudContext ctx)
        {
            //TODO: Use Upsert when available
            object idValue = null;
            var pkField = ctx.ModelDef.PrimaryKey;
            if (pkField != null)
            {
                var propGetter = TypeProperties.Get(dto.GetType()).GetPublicGetter(pkField.Name);
                if (propGetter != null)
                    idValue = propGetter(dto);
            }

            return new ExecValue(idValue, 1);
        }

        internal struct ExecValue
        {
            internal object Id;
            internal long? RowsUpdated;
            public ExecValue(object id, long? rowsUpdated)
            {
                Id = id;
                RowsUpdated = rowsUpdated;
            }
        }
        
        private object ExecAndReturnResponse<Table>(CrudContext context, Func<CrudContext,ExecValue> fn)
        {
            var ignoreEvent = context.Request.Items.ContainsKey(Keywords.IgnoreEvent);
            var trans = context.Events != null && !ignoreEvent
                ? context.Db.OpenTransaction()
                : null;

            using (trans)
            {
                context.SetResult(fn(context));
                if (context.Events != null && !ignoreEvent)
                    context.Events?.Record(context);
                
                trans?.Commit();
            }
            
            if (context.ResponseType == null)
                return null;

            object idValue = null;
                
            var response = context.ResponseType.CreateInstance();
            if (context.IdProp != null && context.Id != null)
            {
                idValue = context.Id.ConvertTo(context.IdProp.PropertyInfo.PropertyType);
                context.IdProp.PublicSetter(response, idValue);
            }
            if (context.CountProp != null && context.RowsUpdated != null)
            {
                context.CountProp.PublicSetter(response, context.RowsUpdated.ConvertTo(context.CountProp.PropertyInfo.PropertyType));
            }

            if (context.ResultProp != null && context.Id != null)
            {
                var result = context.Db.SingleById<Table>(context.Id);
                context.ResultProp.PublicSetter(response, result.ConvertTo(context.ResultProp.PropertyInfo.PropertyType));
            }

            if (context.RowVersionProp != null)
            {
                if (AutoMappingUtils.IsDefaultValue(idValue))
                {
                    var dtoIdGetter = context.RequestIdGetter();
                    if (dtoIdGetter != null)
                        idValue = dtoIdGetter(context.Dto);
                }
                if (AutoMappingUtils.IsDefaultValue(idValue))
                    context.ThrowPrimaryKeyRequiredForRowVersion();
                
                var rowVersion = context.Db.GetRowVersion<Table>(idValue);
                context.RowVersionProp.PublicSetter(response, rowVersion.ConvertTo(context.RowVersionProp.PropertyInfo.PropertyType));
            }
            
            return response;
        }

        private async Task<object> ExecAndReturnResponseAsync<Table>(CrudContext context, Func<CrudContext,Task<ExecValue>> fn)
        {
            var ignoreEvent = context.Request.Items.ContainsKey(Keywords.IgnoreEvent);
            var trans = context.Events != null && !ignoreEvent
                ? context.Db.OpenTransaction()
                : null;

            using (trans)
            {
                context.SetResult(await fn(context).ConfigAwait());
                if (context.Events != null && !ignoreEvent)
                    await context.Events.RecordAsync(context).ConfigAwait();
                
                trans?.Commit();
            }
            
            if (context.ResponseType == null)
                return null;

            object idValue = null;
                
            var response = context.ResponseType.CreateInstance();
            if (context.IdProp != null && context.Id != null)
            {
                idValue = context.Id.ConvertTo(context.IdProp.PropertyInfo.PropertyType);
                context.IdProp.PublicSetter(response, idValue);
            }
            if (context.CountProp != null && context.RowsUpdated != null)
            {
                context.CountProp.PublicSetter(response, context.RowsUpdated.ConvertTo(context.CountProp.PropertyInfo.PropertyType));
            }

            if (context.ResultProp != null && context.Id != null)
            {
                var result = await context.Db.SingleByIdAsync<Table>(context.Id).ConfigAwait();
                context.ResultProp.PublicSetter(response, result.ConvertTo(context.ResultProp.PropertyInfo.PropertyType));
            }

            if (context.RowVersionProp != null)
            {
                if (AutoMappingUtils.IsDefaultValue(idValue))
                {
                    var dtoIdGetter = context.RequestIdGetter();
                    if (dtoIdGetter != null)
                        idValue = dtoIdGetter(context.Dto);
                }

                if (AutoMappingUtils.IsDefaultValue(idValue))
                    context.ThrowPrimaryKeyRequiredForRowVersion();
                
                var rowVersion = await context.Db.GetRowVersionAsync<Table>(idValue).ConfigAwait();
                context.RowVersionProp.PublicSetter(response, rowVersion.ConvertTo(context.RowVersionProp.PropertyInfo.PropertyType));
            }
            
            return response;
        }

        internal bool GetAutoFilterExpressions(CrudContext ctx, Dictionary<string, object> dtoValues, out string expr, out List<object> exprParams)
        {
            var meta = AutoCrudMetadata.Create(ctx.RequestType);
            if (meta.AutoFilters.Count > 0)
            {
                var dialectProvider = ctx.Db.GetDialectProvider();
                var sb = StringBuilderCache.Allocate();
                var exprParamsList = new List<object>();

                //Update's require PK's, Delete's don't need to
                if (dtoValues.TryRemove(meta.ModelDef.PrimaryKey.Name, out var idValue))
                {
                    var idColumn = dialectProvider.GetQuotedColumnName(meta.ModelDef, meta.ModelDef.PrimaryKey);
                    sb.Append(idColumn + " = {0}");
                    exprParamsList.Add(idValue);
                }
                
                var appHost = HostContext.AppHost;
                for (var i = 0; i < meta.AutoFilters.Count; i++)
                {
                    var filter = meta.AutoFilters[i];
                    var dbAttr = meta.AutoFiltersDbFields[i];
                    
                    var fieldDef = meta.ModelDef.GetFieldDefinition(filter.Field);
                    if (fieldDef == null)
                        throw new NotSupportedException($"{ctx.RequestType.Name} '{filter.Field}' AutoFilter was not found on '{ctx.ModelType.Name}'");

                    var quotedColumn = dialectProvider.GetQuotedColumnName(meta.ModelDef, fieldDef);

                    var value = appHost.EvalScriptValue(filter, ctx.Request);
                    
                    var ret = ExprResult.CreateExpression("AND", quotedColumn, value, dbAttr);

                    if (ret != null)
                    {
                        if (sb.Length > 0)
                            sb.Append(" AND ");

                        var exprResult = ret.Value;
                        if (exprResult.Format.IndexOf("{1}", StringComparison.Ordinal) >= 0)
                            throw new NotSupportedException($"SQL Template '{exprResult.Format}' with multiple arguments is not supported");

                        if (exprResult.Values != null)
                        {
                            for (var index = 0; index < exprResult.Values.Length; index++)
                            {
                                sb.Append(exprResult.Format.Replace("{" + index + "}", "{" + exprParamsList.Count + "}"));
                                exprParamsList.Add(exprResult.Values[index]);
                            }
                        }
                    }

                    expr = StringBuilderCache.ReturnAndFree(sb);
                    exprParams = exprParamsList;
                    return true;
                }
            }

            expr = null;
            exprParams = null;
            return false;
        }
        
        private Dictionary<string, object> ResolveDtoValues(IRequest req, object dto, bool skipDefaults=false)
        {
            var dtoValues = dto.ToObjectDictionary();

            var meta = AutoCrudMetadata.Create(dto.GetType());

            foreach (var entry in meta.MapAttrs)
            {
                if (dtoValues.TryRemove(entry.Key, out var value))
                {
                    dtoValues[entry.Value.To] = value;
                }
            }

            List<string> removeKeys = null;
            foreach (var removeDtoProp in meta.RemoveDtoProps)
            {
                removeKeys ??= new List<string>();
                removeKeys.Add(removeDtoProp);
            }

            var appHost = HostContext.AppHost;
            if (skipDefaults || meta.UpdateAttrs.Count > 0 || meta.DefaultAttrs.Count > 0)
            {
                Dictionary<string, object> replaceValues = null;

                foreach (var entry in dtoValues)
                {
                    var isNullable = meta.NullableProps?.Contains(entry.Key) == true;
                    var isDefaultValue = entry.Value == null || (!isNullable && AutoMappingUtils.IsDefaultValue(entry.Value));
                    if (isDefaultValue)
                    {
                        var handled = false;
                        if (meta.DefaultAttrs.TryGetValue(entry.Key, out var defaultAttr))
                        {
                            handled = true;
                            replaceValues ??= new Dictionary<string, object>();
                            replaceValues[entry.Key] = appHost.EvalScriptValue(defaultAttr, req);
                        }
                        if (!handled)
                        {
                            if (skipDefaults ||
                                (meta.UpdateAttrs.TryGetValue(entry.Key, out var attr) &&
                                 attr.Style == AutoUpdateStyle.NonDefaults))
                            {
                                removeKeys ??= new List<string>();
                                removeKeys.Add(entry.Key);
                            }
                        }
                    }
                }
                
                if (replaceValues != null)
                {
                    foreach (var entry in replaceValues)
                    {
                        dtoValues[entry.Key] = entry.Value;
                    }
                }
            }

            if (removeKeys != null)
            {
                foreach (var key in removeKeys)
                {
                    dtoValues.RemoveKey(key);
                }
            }

            foreach (var populateAttr in meta.PopulateAttrs)
            {
                dtoValues[populateAttr.Field] = appHost.EvalScriptValue(populateAttr, req);
            }

            var populatorFn = AutoMappingUtils.GetPopulator(
                typeof(Dictionary<string, object>), meta.DtoType);
            populatorFn?.Invoke(dtoValues, dto);

            IEnumerable<string> asStrings(object o) => o == null
                ? null
                : o is string s
                    ? s.Split(',').Map(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x))
                    : o is IEnumerable<string> e
                        ? e
                        : throw new NotSupportedException($"'{Keywords.Reset}' is not a list of field names");

            var resetField = meta.ModelDef.GetFieldDefinition(Keywords.Reset);
            var reset = resetField == null 
                ? (dtoValues.TryRemove(Keywords.Reset, out var oReset)
                    ? asStrings(oReset)
                    : dtoValues.TryRemove(Keywords.reset, out oReset)
                        ? asStrings(oReset)
                        : null) 
                  ?? asStrings(req.GetParam(Keywords.reset))
                : null;
            
            if (reset != null)
            {
                foreach (var fieldName in reset)
                {
                    var field = meta.ModelDef.GetFieldDefinition(fieldName);
                    if (field == null)
                        throw new NotSupportedException($"Reset field '{fieldName}' does not exist");
                    if (field.IsPrimaryKey)
                        throw new NotSupportedException($"Cannot reset primary key field '{fieldName}'");
                    dtoValues[field.Name] = field.FieldTypeDefaultValue;
                }
            }

            // Ensure RowVersion is always populated if defined on Request DTO
            if (meta.RowVersionGetter != null && !dtoValues.ContainsKey(Keywords.RowVersion))
                dtoValues[Keywords.RowVersion] = default(uint);

            return dtoValues;
        }
        
    }

    public abstract partial class AutoQueryServiceBase
    {
        public virtual object Create<Table>(ICreateDb<Table> dto) => AutoQuery.Create(dto, Request);

        public virtual Task<object> CreateAsync<Table>(ICreateDb<Table> dto) => AutoQuery.CreateAsync(dto, Request);

        public virtual object Update<Table>(IUpdateDb<Table> dto) => AutoQuery.Update(dto, Request);

        public virtual Task<object> UpdateAsync<Table>(IUpdateDb<Table> dto) => AutoQuery.UpdateAsync(dto, Request);

        public virtual object Patch<Table>(IPatchDb<Table> dto) => AutoQuery.Patch(dto, Request);

        public virtual Task<object> PatchAsync<Table>(IPatchDb<Table> dto) => AutoQuery.PatchAsync(dto, Request);

        public virtual object Delete<Table>(IDeleteDb<Table> dto) => AutoQuery.Delete(dto, Request);

        public virtual Task<object> DeleteAsync<Table>(IDeleteDb<Table> dto) => AutoQuery.DeleteAsync(dto, Request);

        public virtual object Save<Table>(ISaveDb<Table> dto) => AutoQuery.Save(dto, Request);

        public virtual Task<object> SaveAsync<Table>(ISaveDb<Table> dto) => AutoQuery.SaveAsync(dto, Request);
    }
}