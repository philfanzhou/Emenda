"""Synthetic, visibly answered worksheets for LIVE vision testing; no mocked analysis."""
from pathlib import Path
import json
from PIL import Image, ImageDraw, ImageFont

root = Path(__file__).resolve().parent
font_path = "/System/Library/Fonts/Supplemental/Arial.ttf"
font = ImageFont.truetype(font_path, 44)
small = ImageFont.truetype(font_path, 32)
heading = ImageFont.truetype(font_path, 52)
answers = []
for page, bounds in enumerate(((1, 20), (21, 40)), 1):
    image = Image.new("RGB", (2000, 3000), "white")
    draw = ImageDraw.Draw(image)
    draw.text((90, 70), "MATHEMATICS WORKSHEET - QA 20261122", font=heading, fill="black")
    draw.text((90, 145), f"Page {page} of 2. Questions {bounds[0]}-{bounds[1]}. Student answers shown below.", font=small, fill="black")
    for offset, number in enumerate(range(bounds[0], bounds[1]+1)):
        x = 90 + (offset % 2) * 960
        y = 250 + (offset // 2) * 265
        expected = number * 2 + 1
        answer = expected - 2 if number % 4 == 1 else expected
        draw.text((x, y), f"{number}.  {number} + {number+1} = ?", font=font, fill="black")
        draw.text((x, y+75), f"Student answer: {answer}", font=font, fill="#183c78")
        draw.line((x, y+195, x+820, y+195), fill="#bbbbbb", width=2)
        answers.append({"number":number,"answer":answer,"expected":expected,"outcome":"incorrect" if answer!=expected else "correct",
                        "page":page,"region":{"x0":x/2000,"y0":(y-15)/3000,"x1":(x+850)/2000,"y1":(y+175)/3000}})
    image.save(root/f"worksheet-40-page-{page}.png")
(root/"worksheet-40-ground-truth.json").write_text(json.dumps({"synthetic":True,"mockedModel":False,"questions":answers},indent=2)+"\n")
print("Rendered 40 answered questions on two pages; 10 incorrect and 30 correct.")
