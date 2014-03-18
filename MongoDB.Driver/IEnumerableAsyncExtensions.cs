using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MongoDB.Driver
{
    /// <summary>
    /// 
    /// </summary>
    public static class IEnumerableAsyncExtensions
    {
        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TSource"></typeparam>
        /// <param name="source"></param>
        /// <returns></returns>
        public static async Task<TSource> FirstOrDefaultAsync<TSource>(
            this IEnumerableAsync<TSource> source)
        {
            using (var e = await source.GetEnumeratorAsync().ConfigureAwait(false))
            {
                if (await e.MoveNextAsync().ConfigureAwait(false))
                {
                    return e.Current;
                }
            }

            return default(TSource);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TSource"></typeparam>
        /// <param name="source"></param>
        /// <param name="predicate"></param>
        /// <returns></returns>
        public static async Task<TSource> FirstOrDefaultAsync<TSource>(
            this IEnumerableAsync<TSource> source, Func<TSource, bool> predicate)
        {
            using (var e = await source.GetEnumeratorAsync().ConfigureAwait(false))
            {
                if (await e.MoveNextAsync().ConfigureAwait(false))
                {
                    if (predicate(e.Current))
                    {
                        return e.Current;
                    }
                }
            }

            return default(TSource);
        }
    }
}