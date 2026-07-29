import tkinter as tk
import launcher_core
import threading


class LauncherUI:


    def __init__(self):

        self.root = tk.Tk()

        self.root.title("Fkonnor Launcher")

        self.root.geometry("400x250")


        config = launcher_core.load_config()

        username = config["player"]["username"]
        memory = config["java"]["memory"]



        self.title = tk.Label(
            self.root,
            text="FKONNOR LAUNCHER",
            font=("Arial", 18)
        )

        self.title.pack(pady=20)



        self.user_label = tk.Label(
            self.root,
            text=f"Jugador: {username}"
        )

        self.user_label.pack()



        self.ram_label = tk.Label(
            self.root,
            text=f"RAM: {memory}"
        )

        self.ram_label.pack()



        self.status = tk.Label(
            self.root,
            text="Estado: listo"
        )

        self.status.pack()



        self.play = tk.Button(
            self.root,
            text="JUGAR",
            width=20,
            height=2,
            command=self.start_verify
        )

        self.play.pack(pady=30)



    def start_verify(self):

        self.play.config(
            state="disabled"
        )


        self.status.config(
            text="Verificando..."
        )


        thread = threading.Thread(
            target=self.verify_thread
        )

        thread.start()



    def verify_thread(self):

        result = launcher_core.verify_client(
            self.update_status
        )


        if result == 0:

            self.root.after(
                0,
                lambda:
                self.status.config(
                    text="Cliente listo"
                )
            )


            self.root.after(
                1000,
                lambda: (
                    self.play.config(
                        text="En ejecución",
                        state="normal",
                    ),
                    launcher_core.launch_game()
                )
            )
        else:

            self.root.after(
                0,
                lambda:
                self.status.config(
                    text="Error verificando"
                )
            )


        self.root.after(
            0,
            lambda:
            self.play.config(
                state="normal"
            )
        )



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