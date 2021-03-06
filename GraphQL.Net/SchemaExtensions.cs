using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace GraphQL.Net
{
    public static class SchemaExtensions
    {
        // This overload is provided to the user so they can shape TArgs with an anonymous type and rely on type inference for type parameters
        // e.g.  AddField("user", new { id = 0 }, (db, args) => db.Users.Where(u => u.Id == args.id));
        public static GraphQLFieldBuilder<TContext, TEntity> AddField<TContext, TArgs, TEntity>(this GraphQLSchema<TContext> context, string name, TArgs argObj, Expression<Func<TContext, TArgs, TEntity>> queryableGetter)
            => AddField(context, name, queryableGetter);

        public static GraphQLFieldBuilder<TContext, TEntity> AddListField<TContext, TArgs, TEntity>(this GraphQLSchema<TContext> context, string name, TArgs argObj, Expression<Func<TContext, TArgs, IEnumerable<TEntity>>> queryableGetter)
            => AddListField(context, name, queryableGetter);

        public static GraphQLFieldBuilder<TContext, TEntity> AddMutation<TContext, TArgs, TEntity>(this GraphQLSchema<TContext> context, string name, TArgs argObj, Expression<Func<TContext, TArgs, TEntity>> queryableGetter, Action<TContext, TArgs> mutation)
            => AddField(context, name, queryableGetter, mutation);

        public static GraphQLFieldBuilder<TContext, TEntity> AddListMutation<TContext, TArgs, TEntity>(this GraphQLSchema<TContext> context, string name, TArgs argObj, Expression<Func<TContext, TArgs, IEnumerable<TEntity>>> queryableGetter, Action<TContext, TArgs> mutation)
            => AddListField(context, name, queryableGetter, mutation);

        // Transform  (db, args) => db.Entities.Where(args)  into  args => db => db.Entities.Where(args)
        private static GraphQLFieldBuilder<TContext, TEntity> AddListField<TContext, TArgs, TEntity>(this GraphQLSchema<TContext> context, string name, Expression<Func<TContext, TArgs, IEnumerable<TEntity>>> queryableGetter, Action<TContext, TArgs> mutation = null)
        {
            var innerLambda = Expression.Lambda<Func<TContext, IEnumerable<TEntity>>>(queryableGetter.Body, queryableGetter.Parameters[0]);
            return context.AddFieldInternal(name, GetFinalQueryFunc<TContext, TArgs, IEnumerable<TEntity>>(innerLambda, queryableGetter.Parameters[1]), ResolutionType.ToList, mutation);
        }

        // Transform  (db, args) => db.Entities.First(args)  into  args => db => db.Entities.First(args)
        private static GraphQLFieldBuilder<TContext, TEntity> AddField<TContext, TArgs, TEntity>(this GraphQLSchema<TContext> context, string name, Expression<Func<TContext, TArgs, TEntity>> queryableGetter, Action<TContext, TArgs> mutation = null)
        {
            var innerLambda = Expression.Lambda<Func<TContext, TEntity>>(queryableGetter.Body, queryableGetter.Parameters[0]);
            var info = GetQueryInfo(innerLambda);
            if (info.ResolutionType != ResolutionType.Unmodified)
                return context.AddFieldInternal(name, GetFinalQueryFunc<TContext, TArgs, IEnumerable<TEntity>>(info.BaseQuery, queryableGetter.Parameters[1]), info.ResolutionType, mutation);
            return context.AddUnmodifiedFieldInternal(name, GetFinalQueryFunc<TContext, TArgs, TEntity>(info.OriginalQuery, queryableGetter.Parameters[1]), mutation);
        }

        private static Func<TArgs, Expression<Func<TContext, TContext, TResult>>> GetFinalQueryFunc<TContext, TArgs, TResult>(Expression<Func<TContext, TResult>> baseExpr, ParameterExpression param = null)
        {
            // TODO: Replace db param here?
            param = param ?? Expression.Parameter(typeof (TArgs), "args");
            var transformedExpr = Expression.Lambda(Expression.Convert(baseExpr.Body, typeof(TResult)), baseExpr.Parameters[0], Expression.Parameter(typeof (TContext), "base"));
            var quoted = Expression.Quote(transformedExpr);
            var final = Expression.Lambda<Func<TArgs, Expression<Func<TContext, TContext, TResult>>>>(quoted, param);
            return final.Compile();
        }

        // Transform  db => db.Entities.Where(args)  into  args => db => db.Entities.Where(args)
        public static GraphQLFieldBuilder<TContext, TEntity> AddListField<TContext, TEntity>(this GraphQLSchema<TContext> context, string name, Expression<Func<TContext, IEnumerable<TEntity>>> queryableGetter)
        {
            return context.AddFieldInternal(name, GetFinalQueryFunc<TContext, object, IEnumerable<TEntity>>(queryableGetter), ResolutionType.ToList, null);
        }

        // Transform  db => db.Entities.Where(args)  into  args => db => db.Entities.Where(args)
        public static GraphQLFieldBuilder<TContext, TEntity> AddField<TContext, TEntity>(this GraphQLSchema<TContext> context, string name, Expression<Func<TContext, TEntity>> queryableGetter)
        {
            var info = GetQueryInfo(queryableGetter);
            if (info.ResolutionType != ResolutionType.Unmodified)
                return context.AddFieldInternal(name, GetFinalQueryFunc<TContext, object, IEnumerable<TEntity>>(info.BaseQuery), info.ResolutionType, null);
            return context.AddUnmodifiedFieldInternal(name, GetFinalQueryFunc<TContext, object, TEntity>(info.OriginalQuery), null);
        }

        private class QueryInfo<TContext, TEntity>
        {
            public Expression<Func<TContext, TEntity>> OriginalQuery;
            public Expression<Func<TContext, IEnumerable<TEntity>>> BaseQuery;
            public ResolutionType ResolutionType;
        }

        private static QueryInfo<TContext, TEntity> GetQueryInfo<TContext, TEntity>(Expression<Func<TContext, TEntity>> queryableGetter)
        {
            var info = new QueryInfo<TContext, TEntity> {OriginalQuery = queryableGetter, ResolutionType = ResolutionType.Unmodified};
            var mce = queryableGetter.Body as MethodCallExpression;
            if (mce == null) return info;

            if (mce.Method.DeclaringType != typeof (Queryable)) return info; // TODO: Enumerable?
            if (!mce.Method.IsStatic) return info;

            switch (mce.Method.Name)
            {
                case "First":
                    info.ResolutionType = ResolutionType.First;
                    break;
                case "FirstOrDefault":
                    info.ResolutionType = ResolutionType.FirstOrDefault;
                    break;
                default:
                    return info;
            }

            var baseQueryable = mce.Arguments[0];
            if (mce.Arguments.Count > 1)
            {
                baseQueryable = Expression.Call(typeof (Queryable), "Where", new[] {typeof (TEntity)}, baseQueryable, mce.Arguments[1]);
            }

            info.BaseQuery = Expression.Lambda<Func<TContext, IEnumerable<TEntity>>>(baseQueryable, queryableGetter.Parameters[0]);

            return info;
        }
    }
}