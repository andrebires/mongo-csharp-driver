using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MongoDB.Driver
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IEnumeratorAsync<out T> : IEnumeratorAsync, IEnumerator<T>
    {

    }


    public interface IEnumeratorAsync : IEnumerator, IDisposable
    {
        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        Task<bool> MoveNextAsync();
    }

    internal class EnumeratorAsyncWrapper<T> : IEnumeratorAsync<T>
    {

        private IEnumerator<T> _enumerator;

        public EnumeratorAsyncWrapper(IEnumerator<T> enumerator)
        {
            _enumerator = enumerator;
        }

        #region IEnumeratorAsync<T> Members

        public Task<bool> MoveNextAsync()
        {
            return Task.FromResult(MoveNext());
        }

        #endregion

        #region IEnumerator<T> Members

        public T Current
        {
            get { return _enumerator.Current; }
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            _enumerator.Dispose();
        }

        #endregion

        #region IEnumerator Members

        object IEnumerator.Current
        {
            get { return ((IEnumerator)_enumerator).Current; }
        }

        public bool MoveNext()
        {
            return _enumerator.MoveNext();
        }

        public void Reset()
        {
            _enumerator.Reset();
        }

        #endregion
    }
}
