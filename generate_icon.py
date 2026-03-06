#!/usr/bin/env python3
"""
TradingBot 프로페셔널 아이콘 생성 스크립트
암호화폐 + AI + 차트를 조합한 모던한 디자인
"""

from PIL import Image, ImageDraw, ImageFont
import os
import math

def create_trading_bot_icon():
    """모던한 트레이딩 봇 아이콘 생성"""
    print("🎨 TradingBot 프로페셔널 아이콘 생성 중...")
    
    # 이미지 캔버스 생성 (1024x1024, 배경 투명)
    size = (1024, 1024)
    image = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # 컬러 팔레트 (모던하고 고급스러운 색상)
    bg_gradient_start = (10, 14, 39)  # 진한 네이비
    bg_gradient_end = (26, 31, 74)     # 미드 네이비
    primary_cyan = (0, 229, 255)       # 밝은 청록색 (#00E5FF)
    secondary_purple = (124, 77, 255)  # 보라색 (#7C4DFF)
    accent_green = (0, 230, 118)       # 초록색 (#00E676)
    gold_yellow = (255, 193, 7)        # 금색 (#FFC107)
    
    # 1. 배경 원형 그라디언트 효과
    center = 512
    for r in range(500, 0, -5):
        # 그라디언트 계산
        progress = r / 500
        color_r = int(bg_gradient_start[0] + (bg_gradient_end[0] - bg_gradient_start[0]) * progress)
        color_g = int(bg_gradient_start[1] + (bg_gradient_end[1] - bg_gradient_start[1]) * progress)
        color_b = int(bg_gradient_start[2] + (bg_gradient_end[2] - bg_gradient_start[2]) * progress)
        alpha = int(255 * (1 - progress * 0.3))
        
        draw.ellipse([center - r, center - r, center + r, center + r], 
                     fill=(color_r, color_g, color_b, alpha))
    
    # 2. 외곽 링 (클린한 테두리)
    draw.ellipse([62, 62, 962, 962], outline=primary_cyan + (255,), width=8)
    draw.ellipse([82, 82, 942, 942], outline=secondary_purple + (180,), width=4)
    
    # 3. 중앙 코인 심볼 (₿/₵ 스타일)
    # 코인 배경
    draw.ellipse([262, 262, 762, 762], fill=(26, 31, 74, 240), outline=primary_cyan + (255,), width=6)
    
    # 심볼 디자인 (B + $ 결합)
    # 세로 라인 (중앙)
    draw.rectangle([490, 320, 534, 704], fill=gold_yellow + (255,))
    
    # B 형태 (오른쪽 곡선들)
    draw.ellipse([490, 320, 650, 450], fill=gold_yellow + (255,))
    draw.ellipse([505, 335, 635, 435], fill=(26, 31, 74, 255))
    
    draw.ellipse([490, 495, 650, 625], fill=gold_yellow + (255,))
    draw.ellipse([505, 510, 635, 610], fill=(26, 31, 74, 255))
    
    # 상하 라인 (돈 기호 느낌)
    draw.rectangle([470, 280, 554, 300], fill=gold_yellow + (255,))
    draw.rectangle([470, 724, 554, 744], fill=gold_yellow + (255,))
    
    # 4. 차트 라인 (상승 추세) - 왼쪽 하단
    chart_points = [
        (150, 850), (200, 800), (250, 820), 
        (300, 730), (350, 760), (400, 650)
    ]
    for i in range(len(chart_points) - 1):
        draw.line([chart_points[i], chart_points[i+1]], 
                  fill=accent_green + (255,), width=8, joint='curve')
    # 차트 포인트
    for point in chart_points:
        draw.ellipse([point[0]-10, point[1]-10, point[0]+10, point[1]+10], 
                     fill=accent_green + (255,))
    
    # 5. AI 뉴럴 네트워크 패턴 (오른쪽 상단)
    nodes = [
        (850, 200), (900, 180), (950, 200),  # 상단 레이어
        (825, 270), (875, 250), (925, 270), (975, 250),  # 중단 레이어
        (850, 340), (900, 320), (950, 340)   # 하단 레이어
    ]
    
    # 연결선
    connections = [
        (0, 3), (0, 4), (1, 4), (1, 5), (2, 5), (2, 6),
        (3, 7), (4, 7), (4, 8), (5, 8), (5, 9), (6, 9)
    ]
    for conn in connections:
        draw.line([nodes[conn[0]], nodes[conn[1]]], 
                  fill=secondary_purple + (120,), width=3)
    
    # 노드
    for node in nodes:
        draw.ellipse([node[0]-8, node[1]-8, node[0]+8, node[1]+8], 
                     fill=secondary_purple + (255,), outline=primary_cyan + (255,), width=2)
    
    # 6. 데이터 흐름 라인 (추상적 디자인 요소)
    # 좌측
    for i in range(5):
        y = 200 + i * 120
        length = 180 - i * 20
        draw.line([(80, y), (80 + length, y)], 
                  fill=primary_cyan + (100 + i * 30,), width=3)
    
    # 우측
    for i in range(5):
        y = 500 + i * 80
        length = 150 - i * 15
        x_start = 944 - length
        draw.line([(x_start, y), (944, y)], 
                  fill=secondary_purple + (100 + i * 30,), width=3)
    
    # 7. 글로우 효과 (중앙 코인 주변)
    for offset in range(20, 0, -2):
        alpha = int(100 * (offset / 20))
        draw.ellipse([262 - offset, 262 - offset, 762 + offset, 762 + offset], 
                     outline=primary_cyan + (alpha,), width=2)
    
    # 경로 설정
    base_dir = r'e:\PROJECT\CoinFF\TradingBot\TradingBot'
    if not os.path.exists(base_dir):
        os.makedirs(base_dir)
    
    ico_path = os.path.join(base_dir, 'trading_bot.ico')
    png_path = os.path.join(base_dir, 'trading_bot.png')
    jpg_path = os.path.join(base_dir, 'trading_bot.jpg')

    # PNG 저장 (투명 배경)
    image.save(png_path, "PNG")
    print(f"✅ PNG 저장 완료: {png_path}")

    # JPG 저장 (검은색 배경 합성)
    bg = Image.new("RGB", image.size, (10, 14, 39))
    bg.paste(image, mask=image.split()[3])
    bg.save(jpg_path, "JPEG", quality=95)
    print(f"✅ JPG 저장 완료: {jpg_path}")
    
    # ICO 파일로 저장 (멀티 사이즈)
    icon_sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
    images = []

    # Pillow 버전에 따른 필터 상수 처리
    try:
        resample = Image.Resampling.LANCZOS
    except AttributeError:
        resample = Image.LANCZOS

    for icon_size in icon_sizes:
        images.append(image.resize(icon_size, resample))

    # 첫 번째 이미지를 저장하면서 나머지 크기들을 포함
    images[0].save(ico_path, format='ICO', append_images=images[1:])

    print(f"✅ 아이콘 생성 완료: {ico_path}")
    print(f"🎉 총 3개 파일 생성: .ico, .png, .jpg")

if __name__ == '__main__':
    create_trading_bot_icon()
