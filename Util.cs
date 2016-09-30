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
        public IEnumerable<T> Keys { get { return map.Keys; } }
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
        public bool TryGetValue(T key, out List<K> value)
        {
            return map.TryGetValue(key, out value);
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
        public IEnumerable<T> Keys { get { return map.Keys; } }
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
        public bool TryGetValue(T key, out HashSet<K> value)
        {
            return map.TryGetValue(key, out value);
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
    public static class StringExtensions
    {
        /// <summary>
        /// Peel the next non-whitespace substring from the front of the given string.
        /// </summary>
        /// <param name="str">The string to peel from</param>
        /// <param name="rest">The rest of the string after the peel</param>
        /// <returns>The peeled string</returns>
        public static String Peel(this String str, out String rest)
        {
            return Peel(str, 0, out rest);
        }

        /// <summary>
        /// Peel the next non-whitespace substring from the given offset of the given string.
        /// </summary>
        /// <param name="str">The string to peel from</param>
        /// <param name="offset">The offset into the string to start peeling from.</param>
        /// <param name="rest">The rest of the string after the peel</param>
        /// <returns>The peeled string</returns>
        public static String Peel(this String str, Int32 offset, out String rest)
        {
            if (str == null)
            {
                rest = null;
                return null;
            }

            Char c;

            //
            // Skip beginning whitespace
            //
            while (true)
            {
                if (offset >= str.Length)
                {
                    rest = null;
                    return null;
                }
                c = str[offset];
                if (!Char.IsWhiteSpace(c)) break;
                offset++;
            }

            Int32 startOffset = offset;

            //
            // Find next whitespace
            //
            while (true)
            {
                offset++;
                if (offset >= str.Length)
                {
                    rest = null;
                    return str.Substring(startOffset);
                }
                c = str[offset];
                if (Char.IsWhiteSpace(c)) break;
            }

            Int32 peelLimit = offset;

            //
            // Remove whitespace till rest
            //
            while (true)
            {
                offset++;
                if (offset >= str.Length)
                {
                    rest = null;
                }
                if (!Char.IsWhiteSpace(str[offset]))
                {
                    rest = str.Substring(offset);
                    break;
                }
            }
            return str.Substring(startOffset, peelLimit - startOffset);
        }
    }
}
