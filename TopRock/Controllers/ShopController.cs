﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stripe;
using TopRock.Models;

namespace TopRock.Controllers
{
    public class ShopController : Controller
    {
        private readonly toprockContext _context;

        // add configuration so controller can read config values from appsettings.json
        private IConfiguration _configuration;

        // constructor - method to create an instance of this class
        // Dependency Injection
        public ShopController(toprockContext context, IConfiguration configuration)
        {
            // accept in an instance of our db connection class and use this object to connect
            _context = context;

            // accept an instance of the configuration object so we can read appsettings
            _configuration = configuration;
        }

        /*Get: /shop*/
        public IActionResult Index()
        {
            // return list of categories for the user to browse
            var categories = _context.Category.OrderBy(c => c.Name).ToList();
            return View(categories);
        }

        /*GET: /browse/catName*/
        public IActionResult Browse(string category)
        {
            // store the selected category name in the ViewBag so we can display in the view heading
            ViewBag.Category = category;

            // get the list of products for the selected category and pass the list to the view
            var products = _context.Product.Where(p => p.Category.Name == category).OrderBy(p => p.Name).ToList();
            return View(products);
        }

        /*GET: /ProdcutDetails/prodName*/
        public IActionResult ProductDetails(string product)
        {
            // use SingleOrDefault to find either 1 exact match or a null object
            var selectedProduct = _context.Product.SingleOrDefault(p => p.Name == product);
            return View(selectedProduct);
        }

        /*POST: /AddToCart*/
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult AddToCart(int Quantity, int ProductId)
        {
            // identity product price
            var product = _context.Product.SingleOrDefault(p => p.ProductId == ProductId);
            var price = product.Price;

            // determine the username;
            var cartUsername = GetCartUsername();

            // check if this user has this product is already in cart. If so, update quantity
            var cartItem = _context.Cart.SingleOrDefault(c => c.ProductId == ProductId && c.Username == cartUsername);

            if (cartItem == null)
            {
                // if product not already in cart, create and save a new Cart object
                var cart = new Cart
                {
                    ProductId = ProductId,
                    Quantity = Quantity,
                    Price = price,
                    Username = cartUsername
                };

                _context.Cart.Add(cart);
            }
            else
            {
                cartItem.Quantity += Quantity; // add the new quantity to the existing quantity
                _context.Update(cartItem);
            }

            _context.SaveChanges();

            //show the cart page
            return RedirectToAction("Cart");
        }

        // check / set Cart username
        private string GetCartUsername()
        {
            // 1. check if we already are storing the username in the user's session
            if (HttpContext.Session.GetString("CartUsername") == null)
            {
                // initialize and empty string variable that we'll later add to the session object
                var cartUsername = "";

                // 2. if no username in session, is user logged in?
                // if yes, use their email for the session variable
                // if no, use the GUID class to make a new ID and store that in the session
                if (User.Identity.IsAuthenticated)
                {
                    cartUsername = User.Identity.Name;
                }
                else
                {
                    cartUsername = Guid.NewGuid().ToString();
                }

                // now store the cartUSername in a session variable
                HttpContext.Session.SetString("CartUsername", cartUsername);
            }

            // send back the username
            return HttpContext.Session.GetString("CartUsername");
        }

        public IActionResult Cart()
        {
            // 1. figure out who the user is
            var cartUsername = GetCartUsername();

            // 2. query the db to get the user's cart items
            var cartItems = _context.Cart.Include(c => c.Product).Where(c => c.Username == cartUsername).ToList();

            // 3. load a view and pass the cart items to it for display
            return View(cartItems);
        }

        public IActionResult RemoveFromCart(int id)
        {
            //get the object the user wants to delete
            var cartItem = _context.Cart.SingleOrDefault(c => c.CartId == id);

            // delete the object
            _context.Cart.Remove(cartItem);
            _context.SaveChanges();

            // redirect to the updated cart page where the deleted item should be gone
            return RedirectToAction("Cart");
        }

        [Authorize]
        public IActionResult Checkout()
        {
            // check if user has been shopping anonymously now that they are logged in
            MigrateCart();
            return View();
        }

        private void MigrateCart()
        {
            // if user has been shopping anonymously, now attach their items to the username
            if (HttpContext.Session.GetString("CartUsername") != User.Identity.Name)
            {
                // get the GUID username and items in cart
                var cartUsername = HttpContext.Session.GetString("CartUsername");
                var cartItems = _context.Cart.Where(c => c.Username == cartUsername);

                // loop through the cart items from this GUID
                foreach (var item in cartItems)
                {
                    // log-in username: User.Identity.Name
                    // check if this log-in user has this product already in cart. If so, get the first product and update quantity
                    var fCartItem = _context.Cart.FirstOrDefault(c => c.ProductId == item.ProductId && c.Username == User.Identity.Name);

                    // if  doesn't exist, migrate the username from GUID to real username in cart
                    if (fCartItem == null)
                    {
                        item.Username = User.Identity.Name;
                        _context.Update(item);
                    }
                    // if exists, update the quantity of this user's first of this product,
                    // and remove the record with GUID username from cart table
                    else
                    {
                        fCartItem.Quantity += item.Quantity; // add the new quantity to the existing quantity
                        _context.Update(fCartItem);
                        _context.Cart.Remove(item);
                    }
                }

                _context.SaveChanges(); // commit all the updates to the db

                // update the session variable from a GUID to the user's email
                HttpContext.Session.SetString("CartUsername", User.Identity.Name);
            }
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Checkout([Bind("FirstName,LastName,Address,City,Province,PostalCode,Phone")] Models.Order order)
        {
            // auto-fill the date, user, and total properties rather than let the user enter these values
            order.OrderDate = DateTime.Now;
            order.UserId = User.Identity.Name;

            var cartItems = _context.Cart.Where(c => c.Username == User.Identity.Name);
            decimal cartTotal = (from c in cartItems
                                 select c.Quantity * c.Price).Sum();

            order.Total = cartTotal;

            HttpContext.Session.SetObject("Order", order);

            return RedirectToAction("Payment");
        }

        [Authorize]
        public IActionResult Payment()
        {
            // set up payment page to show the order total

            // 1. Get the order from te session variable & cast it as an Order object
            var order = HttpContext.Session.GetObject<Models.Order>("Order");

            // 2. Use viebag to display total and pass the amount to Stripe
            ViewBag.Total = order.Total;
            ViewBag.CentsTotal = order.Total * 100;
            ViewBag.PublishableKey = _configuration.GetSection("Stripe")["PublishableKey"];

            ViewBag.Name = order.FirstName + " " + order.LastName;
            ViewBag.Address = order.Address + ", " + order.City + ", " + order.Province + " " + order.PostalCode;
            ViewBag.Phone = order.Phone;
            

            // 1. figure out who the user is
            var cartUsername = GetCartUsername();

            // 2. query the db to get the user's cart items
            var cartItems = _context.Cart.Include(c => c.Product).Where(c => c.Username == cartUsername).ToList();

            return View(cartItems);
        }


        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Payment(string stripeEmail, string stripeToken)
        {
            // send payment to stripe
            StripeConfiguration.ApiKey = _configuration.GetSection("Stripe")["SecretKey"];
            var cartUsername = HttpContext.Session.GetString("CartUsername");
            var cartItems = _context.Cart.Where(c => c.Username == cartUsername);
            var order = HttpContext.Session.GetObject<Models.Order>("Order");

            // new stripe payment attempt
            var customerService = new CustomerService();
            var chargeService = new ChargeService();

            // new customer; email from payment form
            var customer = customerService.Create(new CustomerCreateOptions
            {
                Email = stripeEmail,
                Source = stripeToken
            });

            // new charge using customer created above
            var charge = chargeService.Create(new ChargeCreateOptions
            {
                Amount = Convert.ToInt32(order.Total * 100),
                Description = "Top Rock Purchase",
                Currency = "cad",
                Customer = customer.Id
            });

            // generate and save a new order
            _context.Order.Add(order); 
            _context.SaveChanges(); // The new OrderId PK is populate automatically

            // save order details
            foreach (var item in cartItems)
            {
                var orderDetail = new OrderDetail
                {
                    OrderId = order.OrderId,
                    ProductId = item.ProductId,
                    Quantity = item.Quantity,
                    Price = item.Price
                };

                _context.OrderDetail.Add(orderDetail);
            }
            _context.SaveChanges();

            // delete from cart
            foreach(var item in cartItems)
            {
                _context.Cart.Remove(item);
            }
            _context.SaveChanges();

            // confirmation / receipt for the new OrderId
            // rederict to Details method, in Order controller, and passes in the new id: /Orders/Details/2000
            return RedirectToAction("Details", "Orders", new { id = order.OrderId });
        }
    }
}