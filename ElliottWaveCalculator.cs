public static class ElliottWaveCalculator
{
    // 피보나치 황금 비율
    public const double GoldRatio = 1.618;
    public const double Retrace0618 = 0.618;
    public const double Retrace0382 = 0.382;

    public static bool IsPotentialThirdWave(double p1, double p2, double p3, double currentPrice)
    {
        // p1: 1파 시작점, p2: 1파 고점, p3: 2파 저점
        double wave1Length = p2 - p1;
        double wave2Retracement = (p2 - p3) / wave1Length;
        double currentWave3Length = currentPrice - p3;

        // 엘리엇 규칙 1: 2파는 1파 시작점 아래로 내려갈 수 없음
        if (p3 <= p1) return false;

        // 엘리엇 규칙 2: 2파 되돌림이 통상 0.382 ~ 0.618 사이인지 (필터)
        if (wave2Retracement < 0.2 || wave2Retracement > 0.8) return false;

        // 엘리엇 규칙 3: 3파는 보통 1파의 1.618배 이상 확장됨
        // 현재 가격이 1파의 1.0배를 넘어서며 1.618배를 향해 가고 있는지 확인
        if (currentWave3Length > wave1Length * 1.0 && currentWave3Length < wave1Length * 2.618)
        {
            return true;
        }

        return false;
    }
}