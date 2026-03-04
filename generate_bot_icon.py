from PIL import Image, ImageDraw

def create_trading_bot_character(output_path="TradingBot/trading_bot.jpg"):
    # 이미지 캔버스 생성 (1024x1024, 배경 흰색)
    size = (1024, 1024)
    image = Image.new("RGB", size, "white")
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
    draw.polygon([(512, 212), (480, 100), (544, 100)], fill=(241, 196, 15)) # Yellow/Gold
    draw.ellipse([480, 70, 544, 134], fill=(241, 196, 15))

    # 5. 입 - 차트 선 (지그재그)
    draw.line([(420, 700), (460, 660), (512, 720), (560, 640), (600, 700)], fill="white", width=10)

    # 6. 팔 (심플하게)
    draw.rounded_rectangle([100, 450, 212, 550], radius=20, fill=main_color)
    draw.rounded_rectangle([812, 450, 924, 550], radius=20, fill=main_color)

    # 저장
    image.save(output_path, "JPEG", quality=95)
    print(f"Character image saved to {output_path}")

    # ICO 변환 (멀티 사이즈 지원)
    ico_path = output_path.replace(".jpg", ".ico")
    img = Image.open(output_path)
    # ICO에 포함할 표준 사이즈들
    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
    img.save(ico_path, format="ICO", sizes=sizes)
    print(f"ICO file saved to {ico_path}")

if __name__ == "__main__":
    create_trading_bot_character()
