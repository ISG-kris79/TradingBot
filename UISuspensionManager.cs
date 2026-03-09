using System;

namespace TradingBot
{
    /// <summary>
    /// AI 초기 학습 중 UI 업데이트 병목 해결을 위한 전역 플래그
    /// 
    /// 사용법:
    /// - AI 학습 시작 시: UISuspensionManager.SuspendSignalUpdates(true)
    /// - AI 학습 종료 시: UISuspensionManager.SuspendSignalUpdates(false)
    /// 
    /// 효과:
    /// - 수천 개의 Feature 추출 시 UI DataGrid 업데이트가 일시 중단
    /// - 주문 진입 로직은 영향 없음 (별도 스레드에서 동작)
    /// </summary>
    public static class UISuspensionManager
    {
        private static volatile bool _isSignalUpdateSuspended = false;

        /// <summary>
        /// 현재 시그널 UI 업데이트가 중단된 상태인지 확인
        /// </summary>
        public static bool IsSignalUpdateSuspended => _isSignalUpdateSuspended;

        /// <summary>
        /// 시그널 UI 업데이트 일시 중단/재개
        /// </summary>
        /// <param name="suspend">true=중단, false=재개</param>
        public static void SuspendSignalUpdates(bool suspend)
        {
            _isSignalUpdateSuspended = suspend;
        }

        /// <summary>
        /// AI 초기 학습 래퍼: 자동으로 UI 중단 → 학습 → UI 재개
        /// </summary>
        /// <param name="trainingAction">실제 학습 로직</param>
        /// <returns>학습 결과</returns>
        public static async System.Threading.Tasks.Task<T> ExecuteWithSuspendedUIAsync<T>(
            System.Threading.Tasks.Task<T> trainingAction)
        {
            try
            {
                SuspendSignalUpdates(true);
                return await trainingAction;
            }
            finally
            {
                SuspendSignalUpdates(false);
            }
        }
    }
}
