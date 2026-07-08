using System;

namespace Common
{
    public class RingBuffer
    {
        private byte[] _buffer;
        private int _capacity;
        private int _readPos;  // 읽기 바늘 (소비한 데이터 위치)
        private int _writePos; // 쓰기 바늘 (새로 들어온 데이터 위치)

        // 현재 버퍼에 쌓여서 '읽을 수 있는' 진짜 데이터의 총 크기
        public int DataSize => _writePos - _readPos;

        // 데이터를 더 집어넣을 수 있는 남은 빈 공간의 크기
        public int FreeSize => _capacity - _writePos;

        public RingBuffer(int capacity = 4096)
        {
            _capacity = capacity;
            _buffer = new byte[_capacity];
            _readPos = 0;
            _writePos = 0;
        }

        // 인터넷선에서 데이터가 들어오면 임시 통장에 기록하는 함수
        public bool OnWrite(int size)
        {
            if (size > FreeSize) return false;
            _writePos += size;
            return true;
        }

        // 패킷 하나를 정상적으로 잘라냈으면, 읽은 만큼 바늘을 앞으로 밀어주는 함수
        public bool OnRead(int size)
        {
            if (size > DataSize) return false;
            _readPos += size;
            return true;
        }

        // 현재 읽기 바늘이 가리키는 버퍼의 실제 메모리 주소(배열)를 반환
        public byte[] GetBuffer() => _buffer;
        public int ReadOffset => _readPos;
        public int WriteOffset => _writePos;

        // ★ 핵심 최적화: 읽기 바늘과 쓰기 바늘이 너무 뒤로 가면, 
        // 쓰고 남은 빈 공간을 활용하기 위해 데이터를 맨 앞으로 정렬해 줍니다.
        public void CleanUp()
        {
            int dataSize = DataSize;
            if (dataSize == 0)
            {
                // 데이터가 비었으면 바늘 둘 다 맨 앞으로 초기화
                _readPos = 0;
                _writePos = 0;
            }
            else if (_readPos > _capacity / 2)
            {
                // 읽기 바늘이 절반 넘게 전진했다면, 남은 데이터들을 맨 앞으로 복사해서 당김
                Buffer.BlockCopy(_buffer, _readPos, _buffer, 0, dataSize);
                _readPos = 0;
                _writePos = dataSize;
            }
        }
    }
}