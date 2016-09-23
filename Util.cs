using System;
using System.Collections;
using System.Collections.Generic;

namespace CSharpToD
{
    public struct KeyValues<T, K> : IEnumerable<KeyValuePair<T, List<K>>>
    {
        readonly Dictionary<T, List<K>> map;
        public KeyValues(Boolean igore)
        {
            this.map = new Dictionary<T, List<K>>();
        }
        public void Add(T key, K value)
        {
            List<K> list;
            if (!map.TryGetValue(key, out list))
            {
                list = new List<K>();
                map.Add(key, list);
            }
            list.Add(value);
        }
        public List<K> this[T key]
        {
            get
            {
                return map[key];
            }
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            throw new NotImplementedException();
        }
        public IEnumerator<KeyValuePair<T, List<K>>> GetEnumerator()
        {
            return map.GetEnumerator();

        }
    }
    public struct KeyUniqueValues<T, K> : IEnumerable<KeyValuePair<T, HashSet<K>>>
    {
        readonly Dictionary<T, HashSet<K>> map;
        public KeyUniqueValues(Boolean igore)
        {
            this.map = new Dictionary<T, HashSet<K>>();
        }
        public void Add(T key, K value)
        {
            HashSet<K> set;
            if (!map.TryGetValue(key, out set))
            {
                set = new HashSet<K>();
                map.Add(key, set);
            }
            set.Add(value);
        }
        public HashSet<K> this[T key]
        {
            get
            {
                return map[key];
            }
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            throw new NotImplementedException();
        }
        public IEnumerator<KeyValuePair<T, HashSet<K>>> GetEnumerator()
        {
            return map.GetEnumerator();

        }
    }
}
