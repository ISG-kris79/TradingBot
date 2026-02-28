window.initChart = (container, data) => {
    const chart = LightweightCharts.createChart(container, {
        width: container.clientWidth,
        height: 400,
        layout: {
            backgroundColor: '#1E1E1E',
            textColor: '#D9D9D9',
        },
        grid: {
            vertLines: { color: '#2B2B2B' },
            horzLines: { color: '#2B2B2B' },
        },
        crosshair: {
            mode: LightweightCharts.CrosshairMode.Normal,
        },
        rightPriceScale: {
            borderColor: '#2B2B2B',
        },
        timeScale: {
            borderColor: '#2B2B2B',
            timeVisible: true,
        },
    });

    const candleSeries = chart.addCandlestickSeries({
        upColor: '#4CAF50',
        downColor: '#FF5252',
        borderDownColor: '#FF5252',
        borderUpColor: '#4CAF50',
        wickDownColor: '#FF5252',
        wickUpColor: '#4CAF50',
    });

    const formattedData = data.map(d => ({
        time: Math.floor(new Date(d.openTime).getTime() / 1000),
        open: d.openPrice,
        high: d.highPrice,
        low: d.lowPrice,
        close: d.closePrice,
    }));

    candleSeries.setData(formattedData);

    window.addEventListener('resize', () => {
        chart.resize(container.clientWidth, 400);
    });
};
