using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace GameStorage.Domain.Repositories
{
    public interface IRepository<T> 
    {
        IEnumerable<T> GetList { get; }
        IEnumerable<T> GetFromQuery(Expression<Func<T, bool>> expression);
        void Add(T item);
        void Delete(T item);
        void Update(T item);
        T FindById(int id);
    }
}