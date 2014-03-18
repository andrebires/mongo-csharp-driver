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
    /// <typeparam name="TDocument"></typeparam>
    public interface IEnumerableAsync<TDocument> : IEnumerable<TDocument>
    {
        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        Task<IEnumeratorAsync<TDocument>> GetEnumeratorAsync();
    }
}
