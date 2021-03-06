﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using ReactiveUI;
using SQLite;

namespace Akavache.Sqlite3
{
    public class SqlitePersistentBlobCache : IObjectBlobCache
    {
        public IScheduler Scheduler { get; private set; }
        public IServiceProvider ServiceProvider { get; private set; }

        readonly SQLiteAsyncConnection _connection;
        readonly SQLiteConnectionPool _pool;
        readonly MemoizingMRUCache<string, IObservable<CacheElement>> _inflightCache;
        bool disposed = false;

        public SqlitePersistentBlobCache(string databaseFile, IScheduler scheduler = null, IServiceProvider serviceProvider = null)
        {
            Scheduler = scheduler ?? RxApp.TaskpoolScheduler;
            ServiceProvider = serviceProvider;

            _pool = new SQLiteConnectionPool();
            _connection = new SQLiteAsyncConnection(databaseFile, _pool, storeDateTimeAsTicks: true);
            _connection.CreateTableAsync<CacheElement>();

            _inflightCache = new MemoizingMRUCache<string, IObservable<CacheElement>>((key, _) =>
            {
                return _connection.QueryAsync<CacheElement>("SELECT * FROM CacheElement WHERE Key = ? LIMIT 1;", key)
                    .SelectMany(x =>
                    {
                        return (x.Count == 1) ?
                            Observable.Return(x[0]) : ObservableThrowKeyNotFoundException(key);
                    })
                    .SelectMany(x =>
                    {
                        if (x.Expiration < Scheduler.Now.UtcDateTime) 
                        {
                            Invalidate(key);
                            return ObservableThrowKeyNotFoundException(key);
                        }
                        else 
                        {
                            return Observable.Return(x);
                        }
                    });
            }, 10);
        }

        readonly AsyncSubject<Unit> shutdown = new AsyncSubject<Unit>();
        public IObservable<Unit> Shutdown { get { return shutdown; } }

        public IObservable<Unit> Insert(string key, byte[] data, DateTimeOffset? absoluteExpiration = null)
        {
            if (disposed) return Observable.Throw<Unit>(new ObjectDisposedException("SqlitePersistentBlobCache"));
            lock (_inflightCache) _inflightCache.Invalidate(key);

            var element = new CacheElement()
            {
                Expiration = absoluteExpiration != null ? absoluteExpiration.Value.UtcDateTime : DateTime.MaxValue,
                Key = key,
            };

            var ret = BeforeWriteToDiskFilter(data, Scheduler)
                .Do(x => element.Value = x)
                .SelectMany(x => _connection.InsertAsync(element, "OR REPLACE", typeof(CacheElement)).Select(_ => Unit.Default))
                .Multicast(new AsyncSubject<Unit>());

            ret.Connect();
            return ret;
        }

        public IObservable<byte[]> GetAsync(string key)
        {
            if (disposed) return Observable.Throw<byte[]>(new ObjectDisposedException("SqlitePersistentBlobCache"));
            lock (_inflightCache) {
                return _inflightCache.Get(key)
                    .Select(x => x.Value)
                    .SelectMany(x => AfterReadFromDiskFilter(x, Scheduler))
                    .Finally(() => _inflightCache.Invalidate(key));
            }
        }

        public IEnumerable<string> GetAllKeys()
        {
            if (disposed) throw new ObjectDisposedException("SqlitePersistentBlobCache");

            return _connection.QueryAsync<CacheElement>("SELECT Key FROM CacheElement;")
                .First()
                .Select(x => x.Key)
                .ToArray();
        }

        public IObservable<DateTimeOffset?> GetCreatedAt(string key)
        {
            if (disposed) return Observable.Throw<DateTimeOffset?>(new ObjectDisposedException("SqlitePersistentBlobCache"));
            lock (_inflightCache) 
            {
                return _inflightCache.Get(key)
                    .Select(x => x.Expiration == DateTime.MaxValue ?
                        default(DateTimeOffset?) : new DateTimeOffset(x.Expiration, TimeSpan.Zero))
                    .Catch<DateTimeOffset?, KeyNotFoundException>(_ => Observable.Return(default(DateTimeOffset?)))
                    .Finally(() => _inflightCache.Invalidate(key));
            }           
        }

        public IObservable<Unit> Flush()
        {
            // NB: We don't need to sync metadata when using SQLite3
            return Observable.Return(Unit.Default);
        }

        public IObservable<Unit> Invalidate(string key)
        {
            if (disposed) throw new ObjectDisposedException("SqlitePersistentBlobCache");
            lock(_inflightCache) _inflightCache.Invalidate(key);
            return _connection.ExecuteAsync("DELETE FROM CacheElement WHERE Key=?;", key).Select(_ => Unit.Default);
        }

        public IObservable<Unit> InvalidateAll()
        {
            if (disposed) throw new ObjectDisposedException("SqlitePersistentBlobCache");
            lock(_inflightCache) _inflightCache.InvalidateAll();
            return _connection.ExecuteAsync("DELETE FROM CacheElement;").Select(_ => Unit.Default);
        }

        public IObservable<Unit> InsertObject<T>(string key, T value, DateTimeOffset? absoluteExpiration = null)
        {
            if (disposed) return Observable.Throw<Unit>(new ObjectDisposedException("SqlitePersistentBlobCache"));

            var data = SerializeObject(value);

            lock (_inflightCache) _inflightCache.Invalidate(key);

            var element = new CacheElement()
            {
                Expiration = absoluteExpiration != null ? absoluteExpiration.Value.UtcDateTime : DateTime.MaxValue,
                Key = key,
                TypeName = typeof(T).FullName
            };

            var ret = BeforeWriteToDiskFilter(data, Scheduler)
                .Do(x => element.Value = x)
                .SelectMany(x => _connection.InsertAsync(element, "OR REPLACE", typeof(CacheElement)).Select(_ => Unit.Default))
                .Multicast(new AsyncSubject<Unit>());

            ret.Connect();
            return ret;
        }

        public IObservable<T> GetObjectAsync<T>(string key, bool noTypePrefix = false)
        {
            if (disposed) return Observable.Throw<T>(new ObjectDisposedException("SqlitePersistentBlobCache"));
            lock (_inflightCache) 
            {
                var ret = _inflightCache.Get(key);
                return ret
                    .SelectMany(x => AfterReadFromDiskFilter(x.Value, Scheduler))
                    .SelectMany(x => DeserializeObject<T>(x, this.ServiceProvider ?? BlobCache.ServiceProvider));
            }
        }

        public IObservable<IEnumerable<T>> GetAllObjects<T>()
        {
            if (disposed) return Observable.Throw<IEnumerable<T>>(new ObjectDisposedException("SqlitePersistentBlobCache"));

            return _connection.QueryAsync<CacheElement>("SELECT * FROM CacheElement WHERE TypeName = ?;", typeof(T).FullName)
                .SelectMany(x => x.ToObservable())
                .SelectMany(x => AfterReadFromDiskFilter(x.Value, Scheduler))
                .SelectMany(x => DeserializeObject<T>(x, this.ServiceProvider ?? BlobCache.ServiceProvider))
                .ToList();
        }

        public IObservable<Unit> InvalidateObject<T>(string key)
        {
            if (disposed) throw new ObjectDisposedException("SqlitePersistentBlobCache");
            return Invalidate(key);
        }

        public IObservable<Unit> InvalidateAllObjects<T>()
        {
            if (disposed) throw new ObjectDisposedException("SqlitePersistentBlobCache");
            return _connection.ExecuteAsync("DELETE * FROM CacheElement WHERE TypeName = ?;", typeof(T).FullName)
                .Select(_ => Unit.Default);
        }

        public void Dispose()
        {
            _connection.Shutdown()
                .Finally(() => _pool.Reset())
                .Multicast(shutdown)
                .PermaRef();

            disposed = true;
        }

        /// <summary>
        /// This method is called immediately before writing any data to disk.
        /// Override this in encrypting data stores in order to encrypt the
        /// data.
        /// </summary>
        /// <param name="data">The byte data about to be written to disk.</param>
        /// <param name="scheduler">The scheduler to use if an operation has
        /// to be deferred. If the operation can be done immediately, use
        /// Observable.Return and ignore this parameter.</param>
        /// <returns>A Future result representing the encrypted data</returns>
        protected virtual IObservable<byte[]> BeforeWriteToDiskFilter(byte[] data, IScheduler scheduler)
        {
            if (disposed) return Observable.Throw<byte[]>(new ObjectDisposedException("SqlitePersistentBlobCache"));

            return Observable.Return(data);
        }

        /// <summary>
        /// This method is called immediately after reading any data to disk.
        /// Override this in encrypting data stores in order to decrypt the
        /// data.
        /// </summary>
        /// <param name="data">The byte data that has just been read from
        /// disk.</param>
        /// <param name="scheduler">The scheduler to use if an operation has
        /// to be deferred. If the operation can be done immediately, use
        /// Observable.Return and ignore this parameter.</param>
        /// <returns>A Future result representing the decrypted data</returns>
        protected virtual IObservable<byte[]> AfterReadFromDiskFilter(byte[] data, IScheduler scheduler)
        {
            if (disposed) return Observable.Throw<byte[]>(new ObjectDisposedException("SqlitePersistentBlobCache"));

            return Observable.Return(data);
        }

        byte[] SerializeObject<T>(T value)
        {
            var ms = new MemoryStream();
            var serializer = JsonSerializer.Create(BlobCache.SerializerSettings ?? new JsonSerializerSettings());
            var writer = new BsonWriter(ms);

            var serviceProvider = this.ServiceProvider ?? BlobCache.ServiceProvider;
            if (serviceProvider != null)
            {
                serializer.Converters.Add(new JsonObjectConverter(serviceProvider));
            }

            serializer.Serialize(writer, new ObjectWrapper<T>() { Value = value });
            return ms.ToArray();
        }

        IObservable<T> DeserializeObject<T>(byte[] data, IServiceProvider serviceProvider)
        {
            var serializer = JsonSerializer.Create(BlobCache.SerializerSettings ?? new JsonSerializerSettings());
            var reader = new BsonReader(new MemoryStream(data));

            if (serviceProvider != null) 
            {
                serializer.Converters.Add(new JsonObjectConverter(serviceProvider));
            }

            try 
            {
                var val = serializer.Deserialize<ObjectWrapper<T>>(reader).Value;
                return Observable.Return(val);
            }
            catch (Exception ex) 
            {
                return Observable.Throw<T>(ex);
            }           
        }

        static IObservable<CacheElement> ObservableThrowKeyNotFoundException(string key, Exception innerException = null)
        {
            return Observable.Throw<CacheElement>(
                new KeyNotFoundException(String.Format(CultureInfo.InvariantCulture,
                "The given key '{0}' was not present in the cache.", key), innerException));
        }
    }

    class CacheElement
    {
        [PrimaryKey]
        public string Key { get; set; }

        public string TypeName { get; set; }
        public byte[] Value { get; set; }
        public DateTime Expiration { get; set; }
    }

    interface IObjectWrapper {}
    class ObjectWrapper<T> : IObjectWrapper
    {
        public T Value { get; set; }
    }
}
