import os
import tkinter as tk
import launcher_core
import threading

try:
    from PIL import Image, ImageDraw, ImageFont, ImageTk
except ImportError:
    Image = None
    ImageDraw = None
    ImageFont = None
    ImageTk = None


class RoundedButton(tk.Canvas):
    def __init__(
        self,
        parent,
        text,
        command=None,
        width=280,
        height=70,
        radius=28,
        bg="#4cc06b",
        fg="#ffffff",
        hover="#6fe08f",
        disabled="#2f7a48",
        font=("Boring Time", 15, "bold"),
        font_path=None,
        **kwargs
    ):
        super().__init__(
            parent,
            width=width,
            height=height,
            highlightthickness=0,
            bd=0,
            bg=parent["bg"],
            **kwargs
        )
        self.command = command
        self.width = width
        self.height = height
        self.radius = radius
        self.normal_color = bg
        self.hover_color = hover
        self.disabled_color = disabled
        self.text_color = fg
        self.text_font = font
        self.font_path = font_path
        self.text_value = text
        self.state = "normal"
        self._draw_button(self.normal_color)
        self.bind("<Button-1>", self._on_click)
        self.bind("<Enter>", self._on_enter)
        self.bind("<Leave>", self._on_leave)

    def _load_pil_font(self, size):
        if ImageFont and self.font_path and os.path.isfile(self.font_path):
            try:
                return ImageFont.truetype(self.font_path, size)
            except Exception:
                pass
        if ImageFont:
            try:
                return ImageFont.truetype("arialbd.ttf", size)
            except Exception:
                try:
                    return ImageFont.truetype("arial.ttf", size)
                except Exception:
                    return ImageFont.load_default()
        return None

    def _rounded_rect(self, x1, y1, x2, y2, r):
        return [
            x1 + r, y1,
            x2 - r, y1,
            x2, y1,
            x2, y1 + r,
            x2, y2 - r,
            x2, y2,
            x2 - r, y2,
            x1 + r, y2,
            x1, y2,
            x1, y2 - r,
            x1, y1 + r,
            x1, y1,
        ]

    def _draw_button(self, color):
        self.delete("all")
        self.create_polygon(
            self._rounded_rect(0, 0, self.width, self.height, self.radius),
            smooth=True,
            fill=color,
            outline=""
        )
        self.create_text(
            self.width / 2,
            self.height / 2,
            text=self.text_value,
            fill=self.text_color,
            font=self.text_font,
            tags="label"
        )

    def _on_click(self, event):
        if self.state == "normal" and self.command:
            self.command()

    def _on_enter(self, event):
        if self.state == "normal":
            self._draw_button(self.hover_color)

    def _on_leave(self, event):
        if self.state == "normal":
            self._draw_button(self.normal_color)

    def config(self, **kwargs):
        if "text" in kwargs:
            self.text_value = kwargs.pop("text")
            self.itemconfig("label", text=self.text_value)
        if "state" in kwargs:
            state = kwargs.pop("state")
            self.state = state
            if state == "disabled":
                self._draw_button(self.disabled_color)
            else:
                self._draw_button(self.normal_color)
        return super().config(**kwargs)


class LauncherUI:

    WINDOW_WIDTH = 760
    WINDOW_HEIGHT = 520
    BACKGROUND = "#08121b"

    def __init__(self):

        self.root = tk.Tk()

        self.root.title("Fkonnor Launcher")

        self.root.geometry(f"{self.WINDOW_WIDTH}x{self.WINDOW_HEIGHT}")

        self.root.resizable(False, False)
        self.root.configure(bg=self.BACKGROUND)

        config = launcher_core.load_config()

        username = config["player"]["username"]
        memory = config["java"]["memory"]

        self.canvas = tk.Canvas(
            self.root,
            width=self.WINDOW_WIDTH,
            height=self.WINDOW_HEIGHT,
            bd=0,
            highlightthickness=0,
            bg=self.BACKGROUND
        )
        self.canvas.place(x=0, y=0)
        self._draw_background()

        self.title_text = self.canvas.create_text(
            self.WINDOW_WIDTH / 2,
            50,
            text="FKONNOR LAUNCHER",
            font=("Arial", 24, "bold"),
            fill="#f2f7ff"
        )

        self.user_text = self.canvas.create_text(
            36,
            120,
            text=f"Jugador: {username}",
            font=("Arial", 11),
            fill="#d7e8ff",
            anchor="w"
        )

        self.ram_text = self.canvas.create_text(
            36,
            145,
            text=f"RAM: {memory}",
            font=("Arial", 11),
            fill="#d7e8ff",
            anchor="w"
        )

        self.status_text = self.canvas.create_text(
            36,
            185,
            text="Estado: listo",
            font=("Arial", 13, "bold"),
            fill="#b8f6b2",
            anchor="w"
        )

        self.version_text = self.canvas.create_text(
            36,
            self.WINDOW_HEIGHT - 32,
            text="Version 1.0.0",
            font=("Arial", 10),
            fill="#a9b6be",
            anchor="w"
        )

        self.play_button = RoundedButton(
            self.root,
            text="JUGAR",
            command=self.start_verify,
            width=280,
            height=70,
            radius=34,
            bg="#4cc06b",
            hover="#6fe08f",
            disabled="#2f7a48",
            fg="#ffffff",
            font=("Boring Time", 16, "bold"),
            font_path=os.path.join(os.path.dirname(__file__), "Boring Time.otf")
        )
        self.play_button.place(relx=0.5, y=self.WINDOW_HEIGHT - 90, anchor="center")

    def _draw_background(self):
        image_path = os.path.join(os.path.dirname(__file__), "background.png")
        if os.path.isfile(image_path):
            try:
                if Image and ImageTk:
                    image = Image.open(image_path)
                    image = image.resize((self.WINDOW_WIDTH, self.WINDOW_HEIGHT), Image.LANCZOS)
                    self.background_image = ImageTk.PhotoImage(image)
                else:
                    self.background_image = tk.PhotoImage(file=image_path)
                self.canvas.create_image(0, 0, image=self.background_image, anchor="nw")
                return
            except Exception:
                pass

        gradient_colors = ["#0d1c2e", "#13293f", "#1a3352", "#192f4a"]
        for y in range(0, self.WINDOW_HEIGHT, 8):
            color = gradient_colors[(y // 32) % len(gradient_colors)]
            self.canvas.create_rectangle(
                0,
                y,
                self.WINDOW_WIDTH,
                y + 8,
                outline="",
                fill=color
            )

        for i in range(24):
            x = 40 + (i % 8) * 80
            y = 80 + (i // 8) * 72
            size = 48
            self.canvas.create_rectangle(
                x,
                y,
                x + size,
                y + size,
                fill="#0f2a40",
                outline="",
                stipple="gray50"
            )

        self.canvas.create_rectangle(
            30,
            98,
            self.WINDOW_WIDTH - 30,
            self.WINDOW_HEIGHT - 110,
            outline="#1f4d6c",
            width=2
        )


    def start_verify(self):
        self.play_button.config(text="EN EJECUCIÓN", state="disabled")
        self.status.config(text="Verificando...")
        thread = threading.Thread(target=self.verify_thread, daemon=True)
        thread.start()

    def verify_thread(self):
        result = launcher_core.verify_client(self.update_status)
        if result == 0:
            self.root.after(0, lambda: self.status.config(text="Cliente listo"))
            self.root.after(0, launcher_core.launch_game)
        else:
            self.root.after(0, lambda: self.status.config(text="Error verificando"))
            self.root.after(0, lambda: self.play_button.config(text="JUGAR", state="normal"))



    def update_status(self, text):

        self.root.after(
            0,
            lambda:
            self.status.config(
                text=text
            )
        )



    def run(self):

        self.root.mainloop()