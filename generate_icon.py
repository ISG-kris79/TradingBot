#!/usr/bin/env python3
"""
TradingBot 로봇 캐릭터 아이콘 생성 스크립트
generate_bot_icon.py의 디자인을 적용하여 고품질 아이콘 생성
"""

from PIL import Image, ImageDraw
import os

def create_trading_bot_icon():
    """트레이딩 봇 로봇 캐릭터 아이콘 생성"""
    print("🎨 TradingBot 로봇 아이콘 생성 중...")
    
    # 이미지 캔버스 생성 (1024x1024, 배경 투명)
    size = (1024, 1024)
    image = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # 1. 로봇 몸체 (둥근 사각형 느낌)
    # 파란색 테두리 - 트레이딩의 신뢰성 상징
    main_color = (41, 128, 185) # Belvedere Blue
    draw.rounded_rectangle([212, 212, 812, 812], radius=100, fill=main_color, outline="black", width=10)

    # 2. 로봇 얼굴 (화면창)
    screen_color = (44, 62, 80) # Dark Blue
    draw.rounded_rectangle([312, 312, 712, 612], radius=50, fill=screen_color, outline="white", width=5)

    # 3. 눈 - 상승/하강 캔들 느낌
    # 왼쪽 눈 (초록색 양봉)
    draw.rectangle([400, 400, 450, 520], fill=(46, 204, 113)) # Emerald Green
    draw.line([425, 370, 425, 400], fill="white", width=5)
    draw.line([425, 520, 425, 550], fill="white", width=5)

    # 오른쪽 눈 (빨간색 음봉)
    draw.rectangle([570, 400, 620, 520], fill=(231, 76, 60)) # Alizarin Red
    draw.line([595, 370, 595, 400], fill="white", width=5)
    draw.line([595, 520, 595, 550], fill="white", width=5)

    # 4. 안테나 (비트코인 심볼 느낌의 뿔)
    draw.polygon([(512, 212), (480, 100), (544, 100)], fill=(241, 196, 15), outline="black", width=5) # Yellow/Gold
    draw.ellipse([480, 70, 544, 134], fill=(241, 196, 15), outline="black", width=5)

    # 5. 입 - 차트 선 (지그재그)
    draw.line([(420, 700), (460, 660), (512, 720), (560, 640), (600, 700)], fill="white", width=10, joint='curve')

    # 6. 팔 (심플하게)
    draw.rounded_rectangle([100, 450, 212, 550], radius=20, fill=main_color, outline="black", width=10)
    draw.rounded_rectangle([812, 450, 924, 550], radius=20, fill=main_color, outline="black", width=10)
    
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

    # JPG 저장 (흰색 배경 합성)
    bg = Image.new("RGB", image.size, (255, 255, 255))
    bg.paste(image, mask=image.split()[3])
    bg.save(jpg_path, "JPEG", quality=95)
    print(f"✅ JPG 저장 완료: {jpg_path}")
    
    # ICO 파일로 저장 (멀티 사이즈)
    # 고품질 리사이징(LANCZOS) 적용하여 픽셀 깨짐 방지
    icon_sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
    images = []

    # Pillow 버전에 따른 필터 상수 처리
    resample = Image.Resampling.LANCZOS if hasattr(Image, 'Resampling') else Image.LANCZOS

    for size in icon_sizes:
        images.append(image.resize(size, resample))

    # 첫 번째 이미지를 저장하면서 나머지 크기들을 포함 (append_images 사용)
    images[0].save(ico_path, format='ICO', append_images=images[1:])

    print(f"✅ 아이콘 생성 완료: {ico_path}")

if __name__ == '__main__':
    create_trading_bot_icon()
