using System;
using System.Collections.Generic;

namespace TradingBot.Services
{
    /// <summary>
    /// 고정 크기 순환 배열 — 메모리 재할당 없이 최근 N개 데이터 유지
    ///
    /// 용도: 캔들 데이터, 시세 틱, 지표 히스토리 등
    /// GC 부하 제거: new/resize 없이 배열 인덱스만 이동
    /// Thread-safe: lock 기반 (읽기/쓰기 동시 접근 보호)
    /// </summary>
    public class RingBuffer<T>
    {
        private readonly T[] _buffer;
        private readonly int _capacity;
        private int _head;    // 다음 쓰기 위치
        private int _count;   // 현재 저장된 항목 수
        private readonly object _lock = new();

        public RingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentException("capacity must be > 0");
            _capacity = capacity;
            _buffer = new T[capacity];
        }

        public int Count { get { lock (_lock) return _count; } }
        public int Capacity => _capacity;

        /// <summary>데이터 추가 (가장 오래된 항목 자동 덮어쓰기)</summary>
        public void Add(T item)
        {
            lock (_lock)
            {
                _buffer[_head] = item;
                _head = (_head + 1) % _capacity;
                if (_count < _capacity) _count++;
            }
        }

        /// <summary>시간순 정렬된 리스트 반환 (가장 오래된 → 최신)</summary>
        public List<T> ToList()
        {
            lock (_lock)
            {
                var result = new List<T>(_count);
                if (_count == 0) return result;

                int start = _count < _capacity ? 0 : _head;
                for (int i = 0; i < _count; i++)
                {
                    result.Add(_buffer[(start + i) % _capacity]);
                }
                return result;
            }
        }

        /// <summary>최근 N개 항목 반환</summary>
        public List<T> GetRecent(int n)
        {
            lock (_lock)
            {
                n = Math.Min(n, _count);
                var result = new List<T>(n);
                if (n == 0) return result;

                int start = (_head - n + _capacity) % _capacity;
                for (int i = 0; i < n; i++)
                {
                    result.Add(_buffer[(start + i) % _capacity]);
                }
                return result;
            }
        }

        /// <summary>마지막 항목</summary>
        public T? Last()
        {
            lock (_lock)
            {
                if (_count == 0) return default;
                return _buffer[(_head - 1 + _capacity) % _capacity];
            }
        }

        /// <summary>전체 초기화</summary>
        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_buffer, 0, _capacity);
                _head = 0;
                _count = 0;
            }
        }
    }
}
